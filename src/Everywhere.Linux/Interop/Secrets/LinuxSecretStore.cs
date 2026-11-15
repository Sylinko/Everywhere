using System.Runtime.InteropServices;
using Everywhere.Interop;
using GLib;
using Secret;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Uses libsecret with the released org.freedesktop.Secret.Generic schema and
/// service/account attributes. Metadata enumeration never unlocks or loads secrets.
/// </summary>
public sealed partial class LinuxSecretStore : NativeSecretStore
{
    /// <inheritdoc />
    public override string? GetSecret(string service, string account)
    {
        using var attributes = Attributes.For(service, account);
        using var password = Secret.Internal.Functions.PasswordLookupSync(Attributes.Schema.Handle, attributes.Handle, 0, out var error);
        EnsureSuccess(error);
        if (!password.IsInvalid)
        {
            try
            {
                return password.ConvertToString();
            }
            finally
            {
                // GirCore owns a plain UTF-8 string. Give it to libsecret to wipe before freeing it.
                var pointer = password.DangerousGetHandle();
                password.SetHandleAsInvalid();
                FreePassword(pointer);
            }
        }

        // A declined unlock can also return NULL. Only report absence if no matching item exists.
        if (FindItems(attributes).Any())
        {
            throw new IOException("The desktop credential exists but could not be unlocked or read.");
        }

        return null;
    }

    /// <inheritdoc />
    public override void SetSecret(string service, string account, string secret)
    {
        if (secret.Contains('\0'))
        {
            throw new ArgumentException("The desktop secret service cannot store an embedded NUL.", nameof(secret));
        }

        using var attributes = Attributes.For(service, account);
        try
        {
            if (!Secret.Functions.PasswordStoreSync(Attributes.Schema, attributes, "default", $"{service}/{account}", secret, null))
            {
                throw new IOException("The desktop secret service did not store the credential.");
            }
        }
        catch (GException exception)
        {
            throw new IOException(exception.Message, exception);
        }
    }

    /// <inheritdoc />
    public override void DeleteSecret(string service, string account)
    {
        using var attributes = Attributes.For(service, account);
        foreach (var item in FindItems(attributes))
        {
            // The password-clear convenience API silently skips locked items.
            var isDeleted = Secret.Internal.Item.DeleteSync(item, 0, out var error);
            EnsureSuccess(error);
            if (!isDeleted)
            {
                throw new IOException("The desktop secret service did not delete the credential.");
            }
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> EnumerateAccounts(string service)
    {
        using var attributes = Attributes.For(service);
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in FindItems(attributes))
        {
            using var values = Secret.Internal.Item.GetAttributes(item);
            fixed (byte* accountName = "account"u8)
            {
                var value = GLib.Internal.HashTable.Lookup(values, (nint)accountName);
                if (Marshal.PtrToStringUTF8(value) is { } account)
                {
                    accounts.Add(account);
                }
            }
        }

        return [.. accounts];
    }

    private static IEnumerable<nint> FindItems(HashTable attributes)
    {
        using var items = Secret.Internal.Service.SearchSync(
            0, Attributes.Schema.Handle, attributes.Handle, SearchFlags.All, 0, out var error);
        EnsureSuccess(error);
        try
        {
            for (var node = items.DangerousGetHandle(); node != 0;)
            {
                var entry = Marshal.PtrToStructure<GLib.Internal.ListData>(node);
                yield return entry.Data;
                node = entry.Next;
            }
        }
        finally
        {
            // The search transfers both the list and one GObject reference per item.
            GLib.Internal.List.FreeFull(items, GObject.Internal.Object.Unref);
            items.SetHandleAsInvalid();
        }
    }

    private static void EnsureSuccess(GLib.Internal.ErrorOwnedHandle error)
    {
        using (error)
        {
            if (!error.IsInvalid)
            {
                throw new IOException($"Desktop secret service failed ({error.GetCode()}): {Marshal.PtrToStringUTF8(error.GetMessage())}");
            }
        }
    }

    [LibraryImport("libsecret-1.so.0", EntryPoint = "secret_password_free")]
    private static partial void FreePassword(nint password);

    // Initialize the binding on the first credential operation, not at application startup.
    private static class Attributes
    {
        public static Schema Schema { get; }

        // GLib keeps these function pointers for the lifetime of each table.
        private static readonly GLib.Internal.HashFunc StringHash = GLib.Functions.StrHash;
        private static readonly GLib.Internal.EqualFunc StringEqual = GLib.Functions.StrEqual;
        private static readonly GLib.Internal.DestroyNotify FreeString = Marshal.FreeCoTaskMem;

        static Attributes()
        {
            Secret.Module.Initialize();
            using var fields = NewTable();
            HashTable.Insert(fields, Marshal.StringToCoTaskMemUTF8("service"), (nint)SchemaAttributeType.String);
            HashTable.Insert(fields, Marshal.StringToCoTaskMemUTF8("account"), (nint)SchemaAttributeType.String);
            Schema = Schema.New("org.freedesktop.Secret.Generic", SchemaFlags.None, fields);
        }

        public static HashTable For(string service, string? account = null)
        {
            var table = NewTable();
            InsertString(table, "service", service);
            if (account is not null)
            {
                InsertString(table, "account", account);
            }

            return table;
        }

        private static HashTable NewTable()
        {
            // The table owns its UTF-8 strings. SchemaAttributeType.String is represented by NULL.
            var handle = GLib.Internal.HashTable.NewFull(StringHash, StringEqual, FreeString, FreeString);
            return new HashTable(handle);
        }

        private static void InsertString(HashTable table, string name, string value)
        {
            HashTable.Insert(table, Marshal.StringToCoTaskMemUTF8(name), Marshal.StringToCoTaskMemUTF8(value));
        }
    }
}

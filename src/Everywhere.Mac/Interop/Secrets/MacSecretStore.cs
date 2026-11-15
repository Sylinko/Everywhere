using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Everywhere.Interop;
using ObjCRuntime;
using Security;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Stores generic passwords in the file-based Keychain used by released versions of Everywhere.
/// </summary>
public sealed partial class MacSecretStore : NativeSecretStore
{
    private static readonly nint SecurityLibrary = NativeLibrary.Load(Constants.SecurityLibrary);

    /// <inheritdoc />
    public override string? GetSecret(string service, string account)
    {
        using var query = CreateQuery(service, account);
        using var data = SecKeyChain.QueryAsData(query, wantPersistentReference: false, out var status);
        if (status == SecStatusCode.ItemNotFound)
        {
            return null;
        }

        EnsureSuccess(status);
        if (data is null)
        {
            throw new IOException("Keychain returned no data for a matching credential.");
        }

        var bytes = new ReadOnlySpan<byte>((void*)data.Bytes, checked((int)data.Length));
        return Encoding.UTF8.GetString(bytes);
    }

    /// <inheritdoc />
    public override void SetSecret(string service, string account, string secret)
    {
        using var query = CreateQuery(service, account);
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            using var data = NSData.FromArray(bytes);
            using var changes = new SecRecord();
            changes.ValueData = data;
            var status = SecKeyChain.Update(query, changes);
            if (status == SecStatusCode.ItemNotFound)
            {
                query.ValueData = data;
                status = SecKeyChain.Add(query);
            }

            EnsureSuccess(status);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public override void DeleteSecret(string service, string account)
    {
        using var query = CreateQuery(service, account);
        var status = SecKeyChain.Remove(query);
        if (status != SecStatusCode.ItemNotFound)
        {
            EnsureSuccess(status);
        }
    }

    /// <inheritdoc />
    public override IReadOnlyList<string> EnumerateAccounts(string service)
    {
        using var record = CreateQuery(service);
        using var query = NSMutableDictionary.FromDictionary(record.ToDictionary());

        // QueryAsRecord also requests password data. Enumeration needs only the account names.
        query[GetSecurityString("kSecReturnAttributes")] = NSNumber.FromBoolean(true);
        query[GetSecurityString("kSecMatchLimit")] = GetSecurityString("kSecMatchLimitAll");
        var status = SecItemCopyMatching(query.Handle, out var result);
        if (status == SecStatusCode.ItemNotFound)
        {
            return [];
        }

        EnsureSuccess(status);
        using var items = Runtime.GetNSObject<NSArray>(result, owns: true) ??
            throw new IOException("Keychain returned no attributes for matching credentials.");
        var accountKey = GetSecurityString("kSecAttrAccount");
        var accounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item is NSDictionary attributes && attributes[accountKey] is NSString account)
            {
                accounts.Add(account.ToString());
            }
        }

        return [.. accounts];
    }

    private static SecRecord CreateQuery(string service, string? account = null)
    {
        var query = new SecRecord(SecKind.GenericPassword)
        {
            Service = service,
            UseDataProtectionKeychain = false
        };
        if (account is not null)
        {
            query.Account = account;
        }

        return query;
    }

    private static NSString GetSecurityString(string symbol)
    {
        return Dlfcn.GetStringConstant(SecurityLibrary, symbol) ?? throw new EntryPointNotFoundException(symbol);
    }

    private static void EnsureSuccess(SecStatusCode status)
    {
        if (status != SecStatusCode.Success)
        {
            throw new IOException($"Keychain operation failed with OSStatus {(int)status} ({status}).");
        }
    }

    [LibraryImport(Constants.SecurityLibrary)]
    private static partial SecStatusCode SecItemCopyMatching(nint query, out nint result);
}

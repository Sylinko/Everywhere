using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Everywhere.Interop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Preserves the UTF-8 generic credentials written by GnomeStack, including
/// their service/account target name and enterprise persistence.
/// </summary>
public sealed class WindowsSecretStore : NativeSecretStore
{
    /// <inheritdoc />
    public override unsafe string? GetSecret(string service, string account)
    {
        CREDENTIALW* credential;
        fixed (char* target = $"{service}/{account}")
        {
            if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == (int)WIN32_ERROR.ERROR_NOT_FOUND)
                {
                    return null;
                }

                throw new Win32Exception(error, "Could not read the Windows credential.");
            }
        }

        try
        {
            var bytes = new ReadOnlySpan<byte>(credential->CredentialBlob, checked((int)credential->CredentialBlobSize));
            return Encoding.UTF8.GetString(bytes);
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    /// <inheritdoc />
    public override unsafe void SetSecret(string service, string account, string secret)
    {
        var bytes = Encoding.UTF8.GetBytes(secret);
        try
        {
            if (bytes.Length > PInvoke.CRED_MAX_CREDENTIAL_BLOB_SIZE)
            {
                throw new ArgumentException(
                    $"Windows credentials cannot exceed {PInvoke.CRED_MAX_CREDENTIAL_BLOB_SIZE} UTF-8 bytes.",
                    nameof(secret));
            }

            fixed (char* target = $"{service}/{account}")
            fixed (char* userName = account)
            fixed (byte* data = bytes)
            {
                var credential = new CREDENTIALW
                {
                    Type = CRED_TYPE.CRED_TYPE_GENERIC,
                    TargetName = target,
                    UserName = userName,
                    CredentialBlob = data,
                    CredentialBlobSize = (uint)bytes.Length,
                    Persist = CRED_PERSIST.CRED_PERSIST_ENTERPRISE
                };
                if (!PInvoke.CredWrite(&credential, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not write the Windows credential.");
                }
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <inheritdoc />
    public override void DeleteSecret(string service, string account)
    {
        if (PInvoke.CredDelete($"{service}/{account}", CRED_TYPE.CRED_TYPE_GENERIC))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error != (int)WIN32_ERROR.ERROR_NOT_FOUND)
        {
            throw new Win32Exception(error, "Could not delete the Windows credential.");
        }
    }

    /// <inheritdoc />
    public override unsafe IReadOnlyList<string> EnumerateAccounts(string service)
    {
        var prefix = $"{service}/";
        var count = 0u;
        CREDENTIALW** credentials;
        fixed (char* filter = $"{prefix}*")
        {
            if (!PInvoke.CredEnumerate(filter, 0, &count, &credentials))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == (int)WIN32_ERROR.ERROR_NOT_FOUND)
                {
                    return [];
                }

                throw new Win32Exception(error, "Could not enumerate Windows credentials.");
            }
        }

        try
        {
            var accounts = new List<string>();
            for (var index = 0u; index < count; index++)
            {
                var credential = credentials[index];
                if (credential->Type == CRED_TYPE.CRED_TYPE_GENERIC &&
                    credential->TargetName.ToString() is { } target &&
                    target.StartsWith(prefix, StringComparison.Ordinal))
                {
                    accounts.Add(target[prefix.Length..]);
                }
            }

            return accounts;
        }
        finally
        {
            // CredEnumerate returns one allocation containing the array and all its entries.
            PInvoke.CredFree(credentials);
        }
    }
}

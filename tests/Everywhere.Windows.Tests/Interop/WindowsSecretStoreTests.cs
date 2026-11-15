using Everywhere.Windows.Interop;
using Windows.Win32;
using Windows.Win32.Security.Credentials;

namespace Everywhere.Windows.Tests.Interop;

[Platform("Win")]
public sealed class WindowsSecretStoreTests
{
    [Test]
    public unsafe void LegacyCredential_ReadUpdateAndDelete_UsesTheOriginalTargetAndUtf8Blob()
    {
        var service = $"everywhere-test-{Guid.NewGuid():N}";
        var account = Guid.NewGuid().ToString("N");
        var store = new WindowsSecretStore();
        var legacyBlob = "旧版-🔑"u8;

        // Seed the format used by GnomeStack 0.1.3 independently of the replacement writer.
        fixed (char* target = $"{service}/{account}")
        fixed (char* userName = account)
        fixed (byte* blob = legacyBlob)
        {
            var legacy = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = userName,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)legacyBlob.Length,
                Persist = CRED_PERSIST.CRED_PERSIST_ENTERPRISE
            };
            Assert.That((bool)PInvoke.CredWrite(&legacy, 0), Is.True);
            try
            {
                Assert.That(store.EnumerateAccounts(service), Is.EquivalentTo(new[] { account }));
                Assert.That(store.GetSecret(service, account), Is.EqualTo("旧版-🔑"));
                store.SetSecret(service, account, "新版-🔑");

                CREDENTIALW* updated;
                Assert.That((bool)PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, 0, &updated), Is.True);
                try
                {
                    var bytes = new ReadOnlySpan<byte>(updated->CredentialBlob, checked((int)updated->CredentialBlobSize));
                    Assert.That(bytes.SequenceEqual("新版-🔑"u8), Is.True);
                    Assert.That(updated->Persist, Is.EqualTo(CRED_PERSIST.CRED_PERSIST_ENTERPRISE));
                }
                finally
                {
                    PInvoke.CredFree(updated);
                }

                store.DeleteSecret(service, account);
                Assert.That(store.GetSecret(service, account), Is.Null);
            }
            finally
            {
                PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC, 0);
            }
        }
    }
}

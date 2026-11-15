using Everywhere.Interop;

namespace Everywhere.Configuration.Migrations;

internal static class LegacyApiKeyCredential
{
    public const string ServiceName = "com.sylinko.everywhere.apikeys";

    /// <summary>
    /// Returns true only when the destination holds the legacy value. Both migrations
    /// must establish this before deleting anything, including on retry after a partial move.
    /// </summary>
    public static bool EnsureCurrentCopy(NativeSecretStore store, string account)
    {
        if (store.GetSecret(ServiceName, account) is not { } legacyValue)
        {
            return false;
        }

        if (store.GetSecret(ApiKey.ServiceName, account) is { } currentValue)
        {
            return currentValue == legacyValue;
        }

        store.SetSecret(ApiKey.ServiceName, account, legacyValue);
        return true;
    }
}

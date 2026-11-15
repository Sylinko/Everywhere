using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Interop;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Recovers listed API keys left in the dedicated service used by the 0.5.6 and
/// 0.5.7 Windows and macOS releases. Other profiles may still own unlisted keys.
/// </summary>
public sealed class _20261115200000_0_9_0_canary_20261115_28 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 9, 0, 0, "canary.20261115.28");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateLegacyApiKeys];

    private static bool MigrateLegacyApiKeys(JsonObject root)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return false;
        }

        var accounts = new HashSet<string>(StringComparer.Ordinal);
        AddListedAccounts(GetPathNode(root, "Model.ApiKeys"), accounts);
        AddListedAccounts(GetPathNode(root, "Plugin.WebSearchEngine.ApiKeys"), accounts);
        if (accounts.Count == 0)
        {
            return false;
        }

        var store = NativeSecretStore.Instance;
        var hasMigrated = false;
        foreach (var account in store.EnumerateAccounts(LegacyApiKeyCredential.ServiceName))
        {
            if (!accounts.Contains(account) || !LegacyApiKeyCredential.EnsureCurrentCopy(store, account))
            {
                continue;
            }

            store.DeleteSecret(LegacyApiKeyCredential.ServiceName, account);
            hasMigrated = true;
        }

        return hasMigrated;
    }

    private static void AddListedAccounts(JsonNode? list, HashSet<string> accounts)
    {
        if (list is not JsonArray keys)
        {
            return;
        }

        foreach (var entry in keys)
        {
            if (entry is JsonObject key && key["Id"] is JsonValue value && value.TryGetValue<Guid>(out var id) && id != Guid.Empty)
            {
                accounts.Add(id.ToString("N"));
            }
        }
    }
}

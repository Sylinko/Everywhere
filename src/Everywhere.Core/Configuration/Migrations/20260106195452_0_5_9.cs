using System.Text.Json.Nodes;
using Everywhere.Common;
using Everywhere.Interop;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.5.9 settings changes.
/// It has 2 changes:
/// 1. Migrate API keys stored in Windows Credential Manager from "com.sylinko.everywhere.apikeys/[GUID]" to "com.sylinko.everywhere/[GUID]"
/// 2. Flatten SystemPrompt property of CustomAssistant from a Customizable{string} to a simple string
/// </summary>
public class _20260106195452_0_5_9 : SettingsMigration, ISettingsMigrationMetadata
{
    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(0, 5, 9);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
    [
        MigrateTask1,
        MigrateTask2
    ];

    private static bool MigrateTask1(JsonObject _)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var store = NativeSecretStore.Instance;
        var hasMigrated = false;
        foreach (var account in store.EnumerateAccounts(LegacyApiKeyCredential.ServiceName))
        {
            // The Windows migration predates settings-based selection. Released writers
            // used a non-empty, lowercase N-format GUID for every API-key account.
            if (!Guid.TryParseExact(account, "N", out var id) || id == Guid.Empty || account != id.ToString("N"))
            {
                continue;
            }

            if (!LegacyApiKeyCredential.EnsureCurrentCopy(store, account))
            {
                continue;
            }

            try
            {
                store.DeleteSecret(LegacyApiKeyCredential.ServiceName, account);
            }
            catch (Exception exception)
            {
                // In 0.5.9 cleanup was best-effort; the prompt migration must still run.
                Serilog.Log.Warning(exception, "Could not remove a superseded API-key credential after migration.");
            }

            hasMigrated = true;
        }

        return hasMigrated;
    }

    private static bool MigrateTask2(JsonObject root)
    {
        var customAssistantsNode = GetPathNode(root, "Model.CustomAssistants");
        if (customAssistantsNode is not JsonArray customAssistantsArray) return false;

        var modified = false;
        foreach (var assistantNode in customAssistantsArray)
        {
            if (assistantNode is not JsonObject assistantObj) continue;

            modified |= FlattenCustomizable(assistantObj, "SystemPrompt");
        }

        return modified;
    }
}

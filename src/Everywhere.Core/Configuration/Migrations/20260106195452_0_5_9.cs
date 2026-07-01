using System.Text.Json.Nodes;
using Everywhere.Common;
using GnomeStack.Os.Secrets.Win32;
using ZLinq;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.5.9 settings changes.
/// It has 2 changes:
/// 1. Migrate API keys stored in Windows Credential Manager from "com.sylinko.everywhere.apikeys/[GUID]" to "com.sylinko.everywhere/[GUID]"
/// 2. Flatten SystemPrompt property of CustomAssistant from a Customizable{string} to a simple string
/// </summary>
public class _20260106195452_0_5_9 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 5, 9);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
    [
        MigrateTask1,
        MigrateTask2
    ];

    private static bool MigrateTask1(JsonObject _)
    {
        if (!OperatingSystem.IsWindows()) return false;

        var secretsToMigrate = new Dictionary<string, string>();
        foreach (var credential in WinCredManager.EnumerateCredentials())
        {
            // com.sylinko.everywhere.apikeys/[GUID]
            if (!credential.Service.StartsWith("com.sylinko.everywhere.apikeys/")) continue;
            if (credential.Account is not { } account) continue;

            if (WinCredManager.GetSecret("com.sylinko.everywhere.apikeys", account) is { } secret) secretsToMigrate[account] = secret;

            // Delete original credential
            try
            {
                WinCredManager.DeleteSecret("com.sylinko.everywhere.apikeys", account);
            }
            catch
            {
                // Ignore
            }
        }

        foreach (var (account, secret) in secretsToMigrate.AsValueEnumerable())
        {
            WinCredManager.SetSecret("com.sylinko.everywhere", account, secret);
        }

        return secretsToMigrate.Count > 0;
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
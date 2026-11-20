using System.Text.Json.Nodes;
using Everywhere.Chat.Plugins;
using Everywhere.Common;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Migrates legacy Windows built-in Everything tool rulesets (<c>builtin.everything</c>)
/// to the extracted mod plugin key (<c>mod.everything</c>).
/// </summary>
public sealed class _20261124180000_0_9_0_canary_20261124_31 : SettingsMigration, ISettingsMigrationMetadata
{
    private const string LegacyPluginKey = "builtin.everything";
    private const string ModPluginKey = "mod.everything";

    /// <inheritdoc />
    public static SemanticVersion Version { get; } = new(0, 9, 0, 0, "canary.20261124.31");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateEverythingToolKeys];

    private static bool MigrateEverythingToolKeys(JsonObject root)
    {
        var isModified = MigrateRulesetObject(GetPathNode(root, "Plugin.ToolEnablementRulesets") as JsonObject);
        isModified |= MigrateRulesetObject(GetPathNode(root, "Plugin.ToolBypassApprovalRulesets") as JsonObject);

        if (GetPathNode(root, "Model.CustomAssistants") is JsonArray assistants)
        {
            foreach (var assistantNode in assistants)
            {
                if (assistantNode is JsonObject assistant)
                {
                    isModified |= MigrateRulesetObject(assistant["ToolEnablementRulesets"] as JsonObject);
                }
            }
        }

        return isModified;
    }

    private static bool MigrateRulesetObject(JsonObject? rulesets)
    {
        if (rulesets is null || rulesets.Count == 0)
        {
            return false;
        }

        var oldPluginKey = ToolSettingsKey.ForPlugin(LegacyPluginKey);
        var newPluginKey = ToolSettingsKey.ForPlugin(ModPluginKey);
        var oldFunctionPrefix = ToolSettingsKey.ForFunctionPrefix(LegacyPluginKey);
        var newFunctionPrefix = ToolSettingsKey.ForFunctionPrefix(ModPluginKey);

        var replacements = new List<(string OldKey, string NewKey, JsonNode? Value)>();
        foreach (var (key, value) in rulesets.AsValueEnumerable())
        {
            if (key.Equals(oldPluginKey, StringComparison.OrdinalIgnoreCase))
            {
                replacements.Add((key, newPluginKey, value?.DeepClone()));
            }
            else if (key.StartsWith(oldFunctionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                replacements.Add((key, newFunctionPrefix + key[oldFunctionPrefix.Length..], value?.DeepClone()));
            }
        }

        if (replacements.Count == 0)
        {
            return false;
        }

        foreach (var (oldKey, newKey, value) in replacements)
        {
            rulesets.Remove(oldKey);
            if (!rulesets.ContainsKey(newKey))
            {
                rulesets[newKey] = value;
            }
        }

        return true;
    }
}

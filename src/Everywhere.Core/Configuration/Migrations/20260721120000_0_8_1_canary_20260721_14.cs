using System.Text.Json.Nodes;
using Everywhere.Chat.Plugins;
using Everywhere.Common;

namespace Everywhere.Configuration.Migrations;

public sealed class _20260721120000_0_8_1_canary_20260721_14 : SettingsMigration
{
    private static readonly string[] BuiltInPluginKeys =
    [
        "builtin.essential",
        "builtin.file_system",
        "builtin.officecli",
        "builtin.terminal",
        "builtin.visual_context",
        "builtin.web"
    ];

    public override SemanticVersion Version => new(0, 8, 1, 0, "canary.20260721.14");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateToolSettings];

    private static bool MigrateToolSettings(JsonObject root)
    {
        var modified = TryMoveProperty(root, "Plugin.IsEnabledRecords", "Plugin.ToolEnablementRulesets");
        modified |= TryMoveProperty(root, "Plugin.IsPermissionGrantedRecords", "Plugin.ToolBypassApprovalRulesets");
        modified |= TryMoveProperty(root, "Plugin.Terminal.AutoApprove", "Plugin.Terminal.BypassesApproval");
        modified |= ConvertKeys(GetPathNode(root, "Plugin.ToolEnablementRulesets") as JsonObject, supportsPermissionIds: false);
        var bypassApprovalRulesets = GetPathNode(root, "Plugin.ToolBypassApprovalRulesets") as JsonObject;
        modified |= ConvertKeys(bypassApprovalRulesets, supportsPermissionIds: true);
        modified |= RemoveFileSystemPathApprovals(bypassApprovalRulesets);
        return modified;
    }

    private static bool RemoveFileSystemPathApprovals(JsonObject? records)
    {
        if (records is null) return false;

        var functionPrefix = ToolSettingsKey.ForFunctionPrefix("builtin.file_system");
        var keys = records
            .AsValueEnumerable()
            .Select(static pair => pair.Key)
            .Where(key => key.StartsWith(functionPrefix, StringComparison.OrdinalIgnoreCase) &&
                key.Contains("/permission/|", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var key in keys) records.Remove(key);
        return keys.Length > 0;
    }

    private static bool ConvertKeys(JsonObject? records, bool supportsPermissionIds)
    {
        if (records is null) return false;

        var replacements = new List<(string OldKey, string NewKey, JsonNode? Value)>();
        foreach (var (key, value) in records.AsValueEnumerable())
        {
            if (key.StartsWith("p:", StringComparison.Ordinal) || key.StartsWith("f:", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TrySplitLegacyKey(key, out var pluginKey, out var functionAndPermission)) continue;

            var newKey = functionAndPermission is null ?
                ToolSettingsKey.ForPlugin(pluginKey) :
                CreateFunctionKey(pluginKey, functionAndPermission, supportsPermissionIds);
            replacements.Add((key, newKey, value?.DeepClone()));
        }

        foreach (var (oldKey, newKey, value) in replacements.AsValueEnumerable())
        {
            records.Remove(oldKey);
            records[newKey] = value;
        }

        return replacements.Count > 0;
    }

    private static string CreateFunctionKey(string pluginKey, string functionAndPermission, bool supportsPermissionIds)
    {
        if (!supportsPermissionIds) return ToolSettingsKey.ForFunction(pluginKey, functionAndPermission);

        var separator = functionAndPermission.IndexOf('.');
        return separator < 0 ?
            ToolSettingsKey.ForFunction(pluginKey, functionAndPermission) :
            $"{ToolSettingsKey.ForFunction(pluginKey, functionAndPermission[..separator])}/permission/" +
            ToolSettingsKey.Escape(functionAndPermission[(separator + 1)..]);
    }

    private static bool TrySplitLegacyKey(string key, out string pluginKey, out string? functionAndPermission)
    {
        foreach (var candidate in BuiltInPluginKeys.AsValueEnumerable().OrderByDescending(static candidate => candidate.Length))
        {
            if (key.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                pluginKey = candidate;
                functionAndPermission = null;
                return true;
            }

            if (key.StartsWith(candidate + ".", StringComparison.OrdinalIgnoreCase))
            {
                pluginKey = candidate;
                functionAndPermission = key[(candidate.Length + 1)..];
                return true;
            }
        }

        if (key.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase) && key.Length >= 40 && Guid.TryParse(key.AsSpan(4, 36), out _))
        {
            pluginKey = key[..40];
            functionAndPermission = key.Length == 40 ? null : key[41..];
            return key.Length == 40 || key[40] == '.';
        }

        pluginKey = string.Empty;
        functionAndPermission = null;
        return false;
    }
}

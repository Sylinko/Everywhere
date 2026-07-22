using System.Text.Json;
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
        var modified = TryMoveProperty(root, "Plugin.IsEnabledRecords", "Plugin.ToolEnablement");
        modified |= TryMoveProperty(root, "Plugin.IsPermissionGrantedRecords", "Plugin.ToolAutoApproval");
        modified |= ConvertKeys(GetPathNode(root, "Plugin.ToolEnablement") as JsonObject, supportsPermissionIds: false);
        modified |= ConvertKeys(GetPathNode(root, "Plugin.ToolAutoApproval") as JsonObject, supportsPermissionIds: true);
        Console.WriteLine(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return modified;
    }

    private static bool ConvertKeys(JsonObject? records, bool supportsPermissionIds)
    {
        if (records is null) return false;

        var replacements = new List<(string OldKey, string NewKey, JsonNode? Value)>();
        foreach (var (key, value) in records)
        {
            if (key.StartsWith("plugin/", StringComparison.Ordinal) ||
                key.StartsWith("function/", StringComparison.Ordinal))
            {
                continue;
            }

            if (!TrySplitLegacyKey(key, out var pluginKey, out var functionAndPermission)) continue;

            var newKey = functionAndPermission is null ?
                ToolSettingsKey.ForPlugin(pluginKey) :
                CreateFunctionKey(pluginKey, functionAndPermission, supportsPermissionIds);
            replacements.Add((key, newKey, value?.DeepClone()));
        }

        foreach (var (oldKey, newKey, value) in replacements)
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
        foreach (var candidate in BuiltInPluginKeys.OrderByDescending(static candidate => candidate.Length))
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

        if (key.StartsWith("mcp.", StringComparison.OrdinalIgnoreCase) && key.Length >= 40 &&
            Guid.TryParse(key.AsSpan(4, 36), out _))
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
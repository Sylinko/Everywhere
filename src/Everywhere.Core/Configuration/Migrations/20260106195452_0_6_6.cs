using System.Text.Json.Nodes;
using Everywhere.Common;
using GnomeStack.Os.Secrets.Win32;
using ZLinq;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.6.6 settings changes.
/// It has 1 changes:
/// 1. Set Schema property of CustomAssistant to OpenAI if it is DeepSeek
/// </summary>
public class _20260106195452_0_6_6 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 6, 6);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
    [
        MigrateTask1,
    ];

    private static bool MigrateTask1(JsonObject root)
    {
        var customAssistantsNode = GetPathNode(root, "Model.CustomAssistants");
        if (customAssistantsNode is not JsonArray customAssistantsArray) return false;

        var modified = false;
        foreach (var assistantNode in customAssistantsArray)
        {
            if (assistantNode is not JsonObject assistantObj) continue;

            var schemaNode = GetPathNode(assistantObj, "Schema");
            if (schemaNode is not JsonValue schemaValue) continue;

            if (schemaValue.GetValue<string?>() == "DeepSeek")
            {
                assistantObj["Schema"] = "OpenAI";
                modified = true;
            }
        }

        return modified;
    }
}
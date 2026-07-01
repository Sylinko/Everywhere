using System.Text.Json.Nodes;
using Everywhere.Common;

namespace Everywhere.Configuration.Migrations;

public class _20260614154350_0_8_0 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 8, 0);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateTask1];

    private static bool MigrateTask1(JsonObject root)
    {
        var shortcutNode = GetPathNode(root, "Shortcut");
        if (shortcutNode is not JsonObject shortcutObject) return false;

        var modified = false;
        if (shortcutObject.TryGetPropertyValue("ChatWindow", out var chatWindowNode) && chatWindowNode is JsonObject chatWindowObject)
        {
            MigrateKeyboardShortcut(chatWindowObject, ref modified);
        }
        if (shortcutObject.TryGetPropertyValue("PickVisualElement", out var pickVisualElementNode) && pickVisualElementNode is JsonObject pickVisualElementObject)
        {
            MigrateKeyboardShortcut(pickVisualElementObject, ref modified);
        }
        if (shortcutObject.TryGetPropertyValue("TakeScreenshot", out var takeScreenshotNode) && takeScreenshotNode is JsonObject takeScreenshotObject)
        {
            MigrateKeyboardShortcut(takeScreenshotObject, ref modified);
        }

        return modified;
    }

    private static void MigrateKeyboardShortcut(JsonObject root, ref bool modified)
    {
        // {
        // "Key": "C",
        // "Modifiers": "Control, Shift"
        // }
        // to
        // {
        // "Main": {
        //   "Key": "C",
        //   "Modifiers": "Control, Shift"
        //   }
        // }

        if (root.TryGetPropertyValue("Key", out var keyNode))
        {
            if (!root.TryGetPropertyValue("Main", out var mainNode) || mainNode is null)
            {
                root["Main"] = mainNode = new JsonObject();
            }

            mainNode["Key"] ??= keyNode?.DeepClone();
            root.Remove("Key");
            modified = true;
        }

        if (root.TryGetPropertyValue("Modifiers", out var modifiersNode))
        {
            if (!root.TryGetPropertyValue("Main", out var mainNode) || mainNode is null)
            {
                root["Main"] = mainNode = new JsonObject();
            }

            mainNode["Modifiers"] ??= modifiersNode?.DeepClone();
            root.Remove("Modifiers");
            modified = true;
        }
    }
}
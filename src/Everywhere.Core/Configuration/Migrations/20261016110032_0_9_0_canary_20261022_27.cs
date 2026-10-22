using System.Text.Json.Nodes;
using Everywhere.Common;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Moves the previously hidden selection settings into their dedicated category.
/// </summary>
public sealed class _20261016110032_0_9_0_canary_20261022_27 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 9, 0, 0, "canary.20261022.27");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [MigrateSelection];

    private static bool MigrateSelection(JsonObject root)
    {
        if (root["ChatWindow"] is not JsonObject chat)
        {
            return false;
        }

        var selection = root["TextSelection"] as JsonObject ?? new JsonObject();
        var hasChanges = false;
        Move("AutomaticallyAddTextSelection", "IsAutomaticAttachmentEnabled");
        Move("IsTextSelectionFullscreenExcluded", "IsFullscreenExcluded");

        if (hasChanges && selection.Parent is null)
        {
            root["TextSelection"] = selection;
        }

        return hasChanges;

        void Move(string oldName, string newName)
        {
            if (!chat.Remove(oldName, out var value))
            {
                return;
            }

            if (!selection.ContainsKey(newName))
            {
                selection[newName] = value;
            }

            hasChanges = true;
        }
    }
}
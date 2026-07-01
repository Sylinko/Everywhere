using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.Common;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// This migration handles 0.7.0 settings changes.
/// Task1 migrates to the new model definition structure. For each CustomAssistant,
///   1. Rename "MaxTokens" to "ContextLimit"
///   2. Rename "IsDeepThinkingSupported" to "SupportsReasoning"
///   3. Rename "IsFunctionCallingSupported" to "SupportsToolCall"
///   4. Process "Modalities" node to extract "InputModalities" and "OutputModalities".
/// Task2 migrates settings
///   1. Move "$.Common.Proxy" to "$.Proxy" if exists
///   2. Move "$.Common.Language" to "$.Display.Language" if exists
///   3. Move "$.Common.Theme" to "$.Display.Theme" if exists
///   4. Move "$.Common.FontSize" to "$.Display.FontSize" if exists
///   5. Move "$.ChatWindow.Shortcut" to "$.Shortcut.ChatWindow" if exists
/// </summary>
public class _20260208160256_0_7_0 : SettingsMigration
{
    public override SemanticVersion Version => new(0, 7, 0);

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
    [
        MigrateTask1,
        MigrateTask2,
    ];

    private static bool MigrateTask1(JsonObject root)
    {
        var customAssistantsNode = GetPathNode(root, "Model.CustomAssistants");
        if (customAssistantsNode is not JsonArray customAssistantsArray) return false;

        var modified = false;
        foreach (var assistantNode in customAssistantsArray)
        {
            if (assistantNode is not JsonObject assistantObj) continue;

            // Rename "MaxTokens" to "ContextLimit"
            if (assistantObj.TryGetPropertyValue("MaxTokens", out var maxTokensNode))
            {
                assistantObj["ContextLimit"] = maxTokensNode?.DeepClone();
                assistantObj.Remove("MaxTokens");
                modified = true;
            }

            // Rename "IsDeepThinkingSupported" to "SupportsReasoning"
            if (assistantObj.TryGetPropertyValue("IsDeepThinkingSupported", out var reasoningNode))
            {
                assistantObj["SupportsReasoning"] = reasoningNode?.DeepClone();
                assistantObj.Remove("IsDeepThinkingSupported");
                modified = true;
            }

            // Rename "IsFunctionCallingSupported" to "SupportsToolCall"
            if (assistantObj.TryGetPropertyValue("IsFunctionCallingSupported", out var toolCallNode))
            {
                assistantObj["SupportsToolCall"] = toolCallNode?.DeepClone();
                assistantObj.Remove("IsFunctionCallingSupported");
                modified = true;
            }

            var inputModalities = Modalities.Text;
            if (assistantObj.TryGetPropertyValue("IsImageInputSupported", out var imageSupportedNode))
            {
                if (imageSupportedNode?.GetValue<bool>() == true) inputModalities |= Modalities.Image;
                assistantObj.Remove("IsImageInputSupported");
                modified = true;
            }

            assistantObj["InputModalities"] = JsonValue.Create(inputModalities);
            assistantObj["OutputModalities"] = JsonValue.Create(Modalities.Text);
        }

        return modified;
    }

    private static bool MigrateTask2(JsonObject root)
    {
        var modified = false;
        modified |= TryMoveProperty(root, "Common.Proxy", "Proxy");
        modified |= TryMoveProperty(root, "Common.Language", "Display.Language");
        modified |= TryMoveProperty(root, "Common.Theme", "Display.Theme");
        modified |= TryMoveProperty(root, "Common.FontSize", "Display.FontSize");
        modified |= TryMoveProperty(root, "ChatWindow.Shortcut", "Shortcut.ChatWindow");
        return modified;
    }
}
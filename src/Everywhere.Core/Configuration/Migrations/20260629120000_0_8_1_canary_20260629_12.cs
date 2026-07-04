using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Configuration.Engine;
using ZLinq;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Cleans up settings JSON shapes that predate the current SettingsEngine model.
/// </summary>
/// <remarks>
/// Existing settings files already persisted typed JSON values before
/// SettingsEngine. This migration is intentionally narrow: it simplifies
/// legacy ARGB color objects in custom assistant icons, removes assistant
/// root properties that no longer exist in the active model, and normalizes
/// a few known enum and property-name shapes that predate the current model.
/// </remarks>
public sealed class _20260629120000_0_8_1_canary_20260629_12 : SettingsMigration
{
    private const ModelSpecializations KnownSpecializations =
        ModelSpecializations.TitleGeneration |
        ModelSpecializations.ContextCompression |
        ModelSpecializations.ImageUnderstanding;

    public override SemanticVersion Version => new(0, 8, 1, 0, "canary.20260629.12");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks => [CleanupCustomAssistants, CleanupWebSearchProviders];

    private static ReadOnlySpan<string> LegacyAssistantRootProperties => new[]
    {
        "Temperature",
        "TopP",
        "ReasoningEffort",
        "ThinkingType",
        "SupportsTemperature"
    };

    private static bool CleanupCustomAssistants(JsonObject root)
    {
        if (GetPathNode(root, "Model.CustomAssistants") is not JsonArray assistants)
        {
            return false;
        }

        var modified = false;

        foreach (var assistant in assistants.AsValueEnumerable().OfType<JsonObject>())
        {
            modified |= CleanupOfficialSchema(assistant);
            modified |= CleanupSpecializations(assistant);

            if (assistant.TryGetPropertyValue("Icon", out var iconNode) && iconNode is JsonObject icon)
            {
                modified |= CleanupIconColor(icon, "Foreground");
                modified |= CleanupIconColor(icon, "Background");
            }

            foreach (var propertyName in LegacyAssistantRootProperties)
            {
                modified |= assistant.Remove(propertyName);
            }
        }

        return modified;
    }

    private static bool CleanupOfficialSchema(JsonObject assistant)
    {
        if (!assistant.TryGetPropertyValue("Schema", out var node) ||
            !TryReadString(node, out var schema) ||
            !string.Equals(schema, "Official", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return assistant.Remove("Schema");
    }

    private static bool CleanupSpecializations(JsonObject assistant)
    {
        if (!assistant.TryGetPropertyValue("Specializations", out var node) ||
            !TryReadString(node, out var specializationText) ||
            !TryParseLegacySpecializations(specializationText, out var specializations))
        {
            return false;
        }

        var canonicalNode = SettingsEngineJson.SerializeToNode(specializations, typeof(ModelSpecializations));
        if (JsonNode.DeepEquals(node, canonicalNode))
        {
            return false;
        }

        assistant["Specializations"] = canonicalNode;
        return true;
    }

    private static bool CleanupWebSearchProviders(JsonObject root)
    {
        if (GetPathNode(root, "Plugin.WebSearchEngine.Providers") is not JsonObject providers)
        {
            return false;
        }

        var modified = false;
        foreach (var provider in providers.AsValueEnumerable().Select(static pair => pair.Value).OfType<JsonObject>())
        {
            modified |= FlattenCustomizable(provider, "EndPoint");
        }

        return modified;
    }

    private static bool CleanupIconColor(JsonObject icon, string propertyName)
    {
        if (!icon.TryGetPropertyValue(propertyName, out var node) || node is null || node is not JsonObject colorObject)
        {
            return false;
        }

        if (!TryReadArgbColor(colorObject, out var color))
        {
            return false;
        }

        icon[propertyName] = SettingsEngineJson.SerializeToNode(color, typeof(SerializableColor));
        return true;
    }

    private static bool TryReadArgbColor(JsonObject obj, out SerializableColor color)
    {
        color = default;
        if (obj.Count != 4 ||
            !TryReadByte(obj, "A", out var a) ||
            !TryReadByte(obj, "R", out var r) ||
            !TryReadByte(obj, "G", out var g) ||
            !TryReadByte(obj, "B", out var b))
        {
            return false;
        }

        color = new SerializableColor
        {
            A = a,
            R = r,
            G = g,
            B = b
        };
        return true;
    }

    private static bool TryReadByte(JsonObject obj, string propertyName, out byte value)
    {
        value = default;
        if (!obj.TryGetPropertyValue(propertyName, out var node) || node is null)
        {
            return false;
        }

        try
        {
            var intValue = node.GetValue<int>();
            if (intValue is < byte.MinValue or > byte.MaxValue)
            {
                return false;
            }

            value = (byte)intValue;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseLegacySpecializations(string text, out ModelSpecializations specializations)
    {
        specializations = ModelSpecializations.Default;
        var hasToken = false;

        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            hasToken = true;
            if (!TryParseLegacySpecializationToken(token, out var value))
            {
                return false;
            }

            specializations |= value;
        }

        return hasToken;
    }

    private static bool TryParseLegacySpecializationToken(string token, out ModelSpecializations value)
    {
        value = ModelSpecializations.Default;
        if (Enum.TryParse(token, ignoreCase: true, out value))
        {
            return (value & ~KnownSpecializations) == 0;
        }

        var normalizedToken = token.ToLowerInvariant();
        value = normalizedToken switch
        {
            "none" => ModelSpecializations.Default,
            "title-generation" => ModelSpecializations.TitleGeneration,
            "context-compression" => ModelSpecializations.ContextCompression,
            "image-understanding" => ModelSpecializations.ImageUnderstanding,
            _ => value
        };

        return normalizedToken is "none" or "title-generation" or "context-compression" or "image-understanding";
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        if (node is null)
        {
            return false;
        }

        try
        {
            value = node.GetValue<string>();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
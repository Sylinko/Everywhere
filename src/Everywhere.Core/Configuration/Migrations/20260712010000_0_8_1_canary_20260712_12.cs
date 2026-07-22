using System.Text.Json.Nodes;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Web;
using ZLinq;

namespace Everywhere.Configuration.Migrations;

/// <summary>
/// Normalizes legacy values and MCP plugin shapes that cannot be read by SettingsEngine.
/// </summary>
public sealed class _20260712010000_0_8_1_canary_20260712_12 : SettingsMigration
{
    private const ModelSpecializations KnownSpecializations =
        ModelSpecializations.TitleGeneration |
        ModelSpecializations.ContextCompression |
        ModelSpecializations.ImageUnderstanding;

    public override SemanticVersion Version => new(0, 8, 1, 0, "canary.20260712.12");

    protected override IEnumerable<Func<JsonObject, bool>> MigrationTasks =>
        [CleanupSystemAssistantSpecializations, MigrateMcpTransports, CleanupOfficialSearchDepth];

    private static bool CleanupSystemAssistantSpecializations(JsonObject root)
    {
        if (GetPathNode(root, "SystemAssistant") is not JsonObject systemAssistants)
        {
            return false;
        }

        var modified = false;
        foreach (var assistant in systemAssistants.AsValueEnumerable().Select(static pair => pair.Value).OfType<JsonObject>())
        {
            if (!assistant.TryGetPropertyValue("Specializations", out var node) ||
                !TryReadString(node, out var text) ||
                !TryParseSpecializations(text, out var specializations))
            {
                continue;
            }

            var canonicalValue = specializations.ToString();
            if (string.Equals(text, canonicalValue, StringComparison.Ordinal))
            {
                continue;
            }

            assistant["Specializations"] = canonicalValue;
            modified = true;
        }

        return modified;
    }

    private static bool MigrateMcpTransports(JsonObject root)
    {
        if (GetPathNode(root, "Plugin") is not JsonObject pluginSettings ||
            pluginSettings["McpChatPlugins"] is not JsonArray plugins)
        {
            return false;
        }

        var migratedPlugins = new JsonObject();
        foreach (var plugin in plugins.AsValueEnumerable().OfType<JsonObject>())
        {
            if (!plugin.TryGetPropertyValue("Id", out var idNode) ||
                !TryReadString(idNode, out var idText) ||
                !Guid.TryParse(idText, out var id))
            {
                continue;
            }

            JsonObject? legacyTransport = null;
            string? discriminator = null;
            if (plugin["Stdio"] is JsonObject stdio)
            {
                legacyTransport = stdio;
                discriminator = "stdio";
            }
            else if (plugin["Http"] is JsonObject http)
            {
                legacyTransport = http;
                discriminator = "sse";
            }

            if (legacyTransport is null)
            {
                continue;
            }

            var transport = new JsonObject
            {
                ["$type"] = discriminator
            };
            foreach (var property in legacyTransport)
            {
                if (property.Key == "$type") continue;
                transport[property.Key] = property.Value?.DeepClone();
            }

            migratedPlugins[id.ToString("D")] = transport;
        }

        pluginSettings["McpChatPlugins"] = migratedPlugins;
        return true;
    }

    private static bool CleanupOfficialSearchDepth(JsonObject root)
    {
        if (GetPathNode(root, "Plugin.WebSearchEngine.Providers.Official.Settings") is not JsonObject settings ||
            !settings.TryGetPropertyValue("Depth", out var node) ||
            !TryReadString(node, out var text) ||
            !TryParseSearchDepth(text, out var depth))
        {
            return false;
        }

        var canonicalValue = depth.ToString();
        if (string.Equals(text, canonicalValue, StringComparison.Ordinal))
        {
            return false;
        }

        settings["Depth"] = canonicalValue;
        return true;
    }

    private static bool TryParseSpecializations(string text, out ModelSpecializations specializations)
    {
        specializations = ModelSpecializations.Default;
        var hasToken = false;

        foreach (var token in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            hasToken = true;
            if (!TryParseSpecialization(token, out var value))
            {
                return false;
            }

            specializations |= value;
        }

        return hasToken;
    }

    private static bool TryParseSpecialization(string token, out ModelSpecializations value)
    {
        if (Enum.TryParse(token, ignoreCase: true, out value))
        {
            return (value & ~KnownSpecializations) == 0;
        }

        value = token.ToLowerInvariant() switch
        {
            "none" or "default" => ModelSpecializations.Default,
            "title-generation" => ModelSpecializations.TitleGeneration,
            "context-compression" => ModelSpecializations.ContextCompression,
            "image-understanding" => ModelSpecializations.ImageUnderstanding,
            _ => value
        };

        return token.ToLowerInvariant() is
            "none" or "default" or "title-generation" or "context-compression" or "image-understanding";
    }

    private static bool TryParseSearchDepth(string text, out OfficialConnector.SearchDepth depth)
    {
        if (Enum.TryParse(text, ignoreCase: true, out depth) && Enum.IsDefined(depth))
        {
            return true;
        }

        depth = text.ToLowerInvariant() switch
        {
            "ultra-fast" => OfficialConnector.SearchDepth.UltraFast,
            _ => depth
        };

        return string.Equals(text, "ultra-fast", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadString(JsonNode? node, out string value)
    {
        value = string.Empty;
        try
        {
            value = node?.GetValue<string>() ?? string.Empty;
            return node is not null;
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

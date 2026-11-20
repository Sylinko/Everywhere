using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Provides default JSON serializer and document options used by the settings engine.
/// </summary>
public static class SettingsEngineJson
{
    /// <summary>
    /// Names the root document metadata used by migrations, excluded from runtime settings models.
    /// </summary>
    public const string VersionPropertyName = "Version";

    /// <summary>
    /// Gets the default JSON serializer options configured for application settings persistence.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    /// <summary>
    /// Gets the default JSON document options used when parsing settings files.
    /// </summary>
    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        AllowDuplicateProperties = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    /// <summary>
    /// Serializes a value using <see cref="SerializerOptions"/>.
    /// </summary>
    public static JsonNode? SerializeToNode(object? value, Type type) =>
        JsonSerializer.SerializeToNode(value, type, SerializerOptions);

    /// <summary>
    /// Deserializes a JSON node using <see cref="SerializerOptions"/>.
    /// </summary>
    public static object? Deserialize(JsonNode? node, Type type) =>
        node?.Deserialize(type, SerializerOptions);

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            IgnoreReadOnlyProperties = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            TypeInfoResolver = SettingsJsonSerializerContext.Default,
            WriteIndented = true
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Everywhere.Configuration.Engine;

internal static class SettingsEngineJson
{
    public static JsonSerializerOptions SerializerOptions { get; } = CreateSerializerOptions();

    public static JsonDocumentOptions DocumentOptions { get; } = new()
    {
        AllowTrailingCommas = true,
        AllowDuplicateProperties = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static JsonNode? SerializeToNode(object? value, Type type) =>
        JsonSerializer.SerializeToNode(value, type, SerializerOptions);

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
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            WriteIndented = true
        };

        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly();
        return options;
    }
}
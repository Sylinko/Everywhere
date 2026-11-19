using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Sources;

namespace Everywhere.Modding.Hosting;

/// <summary>
/// Provides source-generated JSON metadata for mod state and HTTP ETag cache persistence.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ModStateDocument))]
[JsonSerializable(typeof(HttpModIndexCacheMetadata))]
internal partial class HostingJsonSerializerContext : JsonSerializerContext
{
    /// <summary>
    /// Gets the canonical serializer context configured for host persistence files.
    /// </summary>
    internal static HostingJsonSerializerContext DefaultOptions { get; } = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        });
}

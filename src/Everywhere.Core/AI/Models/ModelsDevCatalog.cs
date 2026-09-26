using System.Text.Json.Serialization;
using Everywhere.Collections;

namespace Everywhere.AI;

/// <summary>Only the models.dev fields used by the preset catalog are deserialized.</summary>
public sealed class ModelsDevProvider
{
    [JsonPropertyName("models")]
    public Dictionary<string, ModelsDevModel>? Models { get; init; }
}

public sealed record ModelsDevModel
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("tool_call")] public bool ToolCall { get; init; }
    [JsonPropertyName("modalities")] public ModelsDevModalities? Modalities { get; init; }
    [JsonPropertyName("limit")] public ModelsDevLimit? Limit { get; init; }
    [JsonPropertyName("knowledge")] public string? Knowledge { get; init; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("cost")] public ModelsDevCost? Cost { get; init; }
    [JsonPropertyName("reasoning_options")] public ValueArray<ModelsDevReasoningOption> ReasoningOptions { get; init; }
}

public sealed record ModelsDevModalities
{
    [JsonPropertyName("input")] public ValueArray<string> Input { get; init; }
    [JsonPropertyName("output")] public ValueArray<string> Output { get; init; }
}

public sealed record ModelsDevLimit
{
    [JsonPropertyName("context")] public int Context { get; init; }
    [JsonPropertyName("output")] public int Output { get; init; }
}

public sealed record ModelsDevReasoningOption
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("values")] public ValueArray<string> Values { get; init; }
}

public sealed record ModelsDevCost
{
    [JsonPropertyName("input")] public double? Input { get; init; }
    [JsonPropertyName("output")] public double? Output { get; init; }
    [JsonPropertyName("cache_read")] public double? CacheRead { get; init; }
    // Absence enables legacy pricing fallback; an explicitly empty tier list does not.
    [JsonPropertyName("tiers")] public ValueArray<ModelsDevCostTier>? Tiers { get; init; }
    [JsonPropertyName("context_over_200k")] public ModelsDevCost? ContextOver200K { get; init; }
}

public sealed record ModelsDevCostTier
{
    [JsonPropertyName("tier")] public ModelsDevTierBoundary? Tier { get; init; }
    [JsonPropertyName("input")] public double? Input { get; init; }
    [JsonPropertyName("output")] public double? Output { get; init; }
    [JsonPropertyName("cache_read")] public double? CacheRead { get; init; }
}

public sealed record ModelsDevTierBoundary
{
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("size")] public int Size { get; init; }
}

public sealed record ModelsDevCacheEnvelope(int Version, string? ETag, Dictionary<string, ModelsDevProvider> Providers);

// Closed converters avoid runtime generic construction. Their array payloads must be registered
// explicitly because source generation cannot discover types used inside converter methods.
[JsonSourceGenerationOptions(Converters = [
    typeof(ValueArrayJsonConverter<string>),
    typeof(ValueArrayJsonConverter<ModelsDevReasoningOption>),
    typeof(ValueArrayJsonConverter<ModelsDevCostTier>)])]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(ModelsDevReasoningOption[]))]
[JsonSerializable(typeof(ModelsDevCostTier[]))]
[JsonSerializable(typeof(Dictionary<string, ModelsDevProvider>))]
[JsonSerializable(typeof(ModelsDevProvider))]
[JsonSerializable(typeof(ModelsDevCacheEnvelope))]
public sealed partial class ModelsDevJsonContext : JsonSerializerContext;
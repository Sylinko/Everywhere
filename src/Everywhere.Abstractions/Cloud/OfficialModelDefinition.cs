using Everywhere.AI;
using MessagePack;

namespace Everywhere.Cloud;

/// <summary>
/// Describes an official catalog model together with the protocol used by the Everywhere gateway.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record OfficialModelDefinition(
    [property: Key(0)] ModelDefinitionTemplate Model,
    [property: Key(1)] ModelProviderSchema Schema
)
{
    /// <summary>
    /// Infers the protocol used by catalog responses and settings written before official models exposed a schema.
    /// </summary>
    public static ModelProviderSchema InferLegacySchemaFromModelId(string? modelId)
    {
        var separatorIndex = modelId?.IndexOf('/') ?? -1;
        var provider = separatorIndex > 0 && modelId is not null ? modelId[..separatorIndex] : modelId;
        return provider?.ToLowerInvariant() switch
        {
            "openai" => ModelProviderSchema.OpenAIResponses,
            "google" => ModelProviderSchema.Google,
            "anthropic" or "minimax" => ModelProviderSchema.Anthropic,
            "mistral" => ModelProviderSchema.Mistral,
            _ => ModelProviderSchema.OpenAI
        };
    }
}

/// <summary>
/// Persists one complete official catalog revision together with its HTTP validator.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record OfficialModelCatalogCache(
    [property: Key(0)] int Version,
    [property: Key(1)] string? EntityTag,
    [property: Key(2)] OfficialModelDefinition[] Definitions
);
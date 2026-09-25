using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.AI;
using ZLinq;

namespace Everywhere.Cloud;

internal sealed partial class OfficialModelCatalogClient(IHttpClientFactory httpClientFactory)
    : ModelCatalogHttpClient<OfficialModelDefinition[]>(httpClientFactory, nameof(ICloudClient))
{
    protected override HttpRequestMessage CreateRequest() =>
        new(HttpMethod.Get, $"{CloudConstants.AIGatewayBaseUrl}/v1/models");

    protected override OfficialModelDefinition[] ReadResponse(HttpResponseMessage response, ReadOnlySpan<byte> content)
    {
        // TODO: Verify and standardize the official catalog server's ETag generation, conditional
        // request behavior, Retry-After format, and retryable error contract before relying on
        // source-specific retry timing here.
        var payload = JsonSerializer.Deserialize<ApiPayload<IReadOnlyList<CloudModelDefinition>>>(
                content,
                ModelsResponseJsonSerializerContext.Default.Options) ??
            throw new HttpRequestException(HttpRequestError.InvalidResponse, statusCode: response.StatusCode);
        if (!payload.Success)
        {
            var retryAfter = response.Headers.RetryAfter?.Date ??
                (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
            throw new ModelCatalogHttpException(response.StatusCode, retryAfter, payload.ToString());
        }

        return payload.EnsureData().AsValueEnumerable().Select(model => model.ToOfficialModelDefinition()).ToArray();
    }

    /// <summary>
    /// Wire representation returned by the official gateway. Schema is nullable only because older gateway
    /// deployments did not include it; conversion immediately resolves the legacy value.
    /// </summary>
    private sealed record CloudModelDefinition(
        [property: JsonPropertyName("id")] string ModelId,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("icon")] string Icon,
        [property: JsonPropertyName("description")] JsonDynamicLocaleKey? DescriptionKey,
        [property: JsonPropertyName("toolCall")] bool SupportsToolCall,
        [property: JsonPropertyName("knowledge")] string? KnowledgeCutoff,
        [property: JsonPropertyName("releaseDate")] string? ReleaseDate,
        [property: JsonPropertyName("deprecationDate")] string? DeprecationDate,
        [property: JsonPropertyName("modalities")] CloudModelModalities Modalities,
        [property: JsonPropertyName("specializations")] IReadOnlyList<string>? Specializations,
        [property: JsonPropertyName("limit")] CloudModelLimitInfo LimitInfo,
        [property: JsonPropertyName("pricing")] CloudModelPricing Pricing,
        [property: JsonPropertyName("quotaLimited")] bool IsQuotaLimited,
        [property: JsonPropertyName("schema")] string Schema
    )
    {
        public OfficialModelDefinition ToOfficialModelDefinition()
        {
            var model = new ModelDefinitionTemplate
            {
                ModelId = ModelId,
                Name = Name,
                SupportsToolCall = SupportsToolCall,
                KnowledgeCutoff = DateOnly.TryParse(KnowledgeCutoff, out var knowledgeDate) ? knowledgeDate : null,
                ReleaseDate = DateOnly.TryParse(ReleaseDate, out var releaseDate) ? releaseDate : null,
                DeprecationDate = DateOnly.TryParse(DeprecationDate, out var deprecationDate) ? deprecationDate : null,
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                Specializations = ConvertSpecializations(Specializations),
                ContextLimit = LimitInfo.Context,
                OutputLimit = LimitInfo.Output,
                IconUrl = Icon,
                DescriptionKey = DescriptionKey,
                Pricing = ConvertPricing(Pricing),
                IsQuotaLimited = IsQuotaLimited
            };
            return new OfficialModelDefinition(model, ConvertSchema(Schema, ModelId));
        }

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) =>
            modalityStrings.AsValueEnumerable().Aggregate(
                AI.Modalities.None,
                (current, modality) => current | modality.ToLower() switch
                {
                    "text" => AI.Modalities.Text,
                    "image" => AI.Modalities.Image,
                    "audio" => AI.Modalities.Audio,
                    "video" => AI.Modalities.Video,
                    "pdf" => AI.Modalities.Pdf,
                    _ => AI.Modalities.None
                });

        private static ModelSpecializations ConvertSpecializations(IReadOnlyList<string>? specializationStrings)
        {
            if (specializationStrings is null) return ModelSpecializations.Default;

            return specializationStrings.AsValueEnumerable().Aggregate(
                ModelSpecializations.Default,
                (current, specialization) => current | specialization.ToLower() switch
                {
                    "title-generation" => ModelSpecializations.TitleGeneration,
                    "context-compression" => ModelSpecializations.ContextCompression,
                    "image-understanding" => ModelSpecializations.ImageUnderstanding,
                    _ => ModelSpecializations.Default
                });
        }

        private static ModelPricing ConvertPricing(CloudModelPricing pricing)
        {
            const double CreditsMultiplier = 0.01d; // Convert from "per MTokens" to "per Token"
            var tiers = pricing.AsValueEnumerable().Select(tier => new PricingTier(
                tier.Threshold,
                new TokenPricing(
                    tier.Pricing.Input * CreditsMultiplier,
                    tier.Pricing.Output * CreditsMultiplier,
                    tier.Pricing.CachedInput * CreditsMultiplier))).ToArray();
            return new ModelPricing(tiers, ModelPricingUnit.MCreditPerMToken);
        }

        private static ModelProviderSchema ConvertSchema(string schema, string modelId) => schema switch
        {
            "openai" => ModelProviderSchema.OpenAI,
            "openai-responses" => ModelProviderSchema.OpenAIResponses,
            "anthropic" => ModelProviderSchema.Anthropic,
            "google" => ModelProviderSchema.Google,
            "mistral" => ModelProviderSchema.Mistral,
            _ => OfficialModelDefinition.InferLegacySchemaFromModelId(modelId)
        };
    }

    private sealed record CloudModelModalities(
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("output")] IReadOnlyList<string> Output
    );

    private sealed record CloudModelLimitInfo(
        [property: JsonPropertyName("context")] int Context,
        [property: JsonPropertyName("input")] int Input = 0,
        [property: JsonPropertyName("output")] int Output = 0
    );

    private sealed record CloudTokenPricing(
        [property: JsonPropertyName("input")] long Input,
        [property: JsonPropertyName("output")] long Output,
        [property: JsonPropertyName("cachedInput")] long CachedInput
    );

    private sealed record CloudPricingTier(
        [property: JsonPropertyName("threshold")] long Threshold,
        [property: JsonPropertyName("pricing")] CloudTokenPricing Pricing
    );

    private sealed class CloudModelPricing : List<CloudPricingTier>;

    [JsonSerializable(typeof(ApiPayload<IReadOnlyList<CloudModelDefinition>>))]
    private sealed partial class ModelsResponseJsonSerializerContext : JsonSerializerContext;
}
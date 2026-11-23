using System.Globalization;
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
        ApiPayload<IReadOnlyList<CloudModelDefinition>>? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ApiPayload<IReadOnlyList<CloudModelDefinition>>>(
                content,
                ModelsResponseJsonSerializerContext.Default.Options);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw CreateHttpException(response);
        }

        if (!response.IsSuccessStatusCode || payload?.Success == false)
        {
            throw CreateHttpException(response, payload?.ToString());
        }

        return (payload ?? throw new HttpRequestException(HttpRequestError.InvalidResponse, statusCode: response.StatusCode))
            .EnsureData()
            .AsValueEnumerable()
            .Select(model => model.ToOfficialModelDefinition())
            .ToArray();
    }

    private static ModelCatalogHttpException CreateHttpException(HttpResponseMessage response, string? message = null)
    {
        var retryAfter = response.Headers.RetryAfter?.Date ??
            (response.Headers.RetryAfter?.Delta is { } delta ? DateTimeOffset.UtcNow + delta : null);
        return new ModelCatalogHttpException(response.StatusCode, retryAfter, message);
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
        [property: JsonPropertyName("schema")] string Schema,
        [property: JsonPropertyName("reasoningOptions")] IReadOnlyList<CloudReasoningOption>? ReasoningOptions = null
    )
    {
        public OfficialModelDefinition ToOfficialModelDefinition()
        {
            var model = new ModelDefinitionTemplate
            {
                ModelId = ModelId,
                Name = Name,
                SupportsToolCall = SupportsToolCall,
                KnowledgeCutoff = ParseDate(KnowledgeCutoff),
                ReleaseDate = ParseDate(ReleaseDate),
                DeprecationDate = ParseDate(DeprecationDate),
                InputModalities = ConvertModalities(Modalities.Input),
                OutputModalities = ConvertModalities(Modalities.Output),
                Specializations = ConvertSpecializations(Specializations),
                ContextLimit = LimitInfo.Context,
                OutputLimit = LimitInfo.Output,
                IconUrl = Icon,
                DescriptionKey = DescriptionKey,
                Pricing = ConvertPricing(Pricing),
                IsQuotaLimited = IsQuotaLimited,
                ReasoningEffortValues = [.. ConvertReasoningEffortValues(ReasoningOptions)]
            };
            return new OfficialModelDefinition(model, ConvertSchema(Schema, ModelId));
        }

        private static DateOnly? ParseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        }

        private static string[] ConvertReasoningEffortValues(IReadOnlyList<CloudReasoningOption>? options)
        {
            if (options is null) return [];

            string[]? result = null;
            foreach (var option in options)
            {
                if (!string.Equals(option.Type, "effort", StringComparison.Ordinal) || option.Values is null) continue;

                var values = option.Values
                    .AsValueEnumerable()
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value.Trim())
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                if (values.Length == 0) continue;

                if (result is null) result = values;
                else if (!result.SequenceEqual(values, StringComparer.Ordinal)) return [];
            }

            return result ?? [];
        }

        private static Modalities ConvertModalities(IReadOnlyList<string> modalityStrings) =>
            modalityStrings.AsValueEnumerable()
                .Aggregate(
                    AI.Modalities.None,
                    (current, modality) => current | modality.ToLowerInvariant() switch
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

            return specializationStrings.AsValueEnumerable()
                .Aggregate(
                    ModelSpecializations.Default,
                    (current, specialization) => current | specialization.ToLowerInvariant() switch
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
            var tiers = pricing.AsValueEnumerable()
                .Select(tier => new PricingTier(
                    tier.Threshold,
                    new TokenPricing(
                        tier.Pricing.Input * CreditsMultiplier,
                        tier.Pricing.Output * CreditsMultiplier,
                        tier.Pricing.CachedInput * CreditsMultiplier)))
                .ToArray();
            return new ModelPricing(tiers, ModelPricingUnit.MCreditPerMToken);
        }

        private static ModelProviderSchema ConvertSchema(string schema, string modelId) => schema switch
        {
            "openai" => ModelProviderSchema.OpenAI,
            "openai-responses" => ModelProviderSchema.OpenAIResponses,
            "anthropic" => ModelProviderSchema.Anthropic,
            "google" => ModelProviderSchema.Google,
            "google-interactions" => ModelProviderSchema.GoogleInteractions,
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

    private sealed record CloudReasoningOption(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("values")] IReadOnlyList<string>? Values = null
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

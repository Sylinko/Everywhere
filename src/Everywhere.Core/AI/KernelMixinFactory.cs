using System.Text.Json;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

/// <summary>
/// A factory for creating instances of <see cref="KernelMixin"/>.
/// </summary>
public sealed class KernelMixinFactory(
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory
) : IKernelMixinFactory
{
    /// <summary>
    /// Creates a new instance of <see cref="KernelMixin"/>.
    /// </summary>
    /// <param name="assistant"></param>
    /// <returns>A new instance of <see cref="KernelMixin"/>.</returns>
    /// <exception cref="HandledChatException">Thrown if the model provider or definition is not found or not supported.</exception>
    public KernelMixin Create(Assistant assistant)
    {
        var sourceConfiguration = assistant.Configuration;
        AssistantConfiguration configuration;
        ModelSchemaOptions? schemaOptions;
        TimeSpan timeout;
        int maxRetries;

        lock (sourceConfiguration)
        {
            configuration = AssistantSnapshotMapper.Copy(sourceConfiguration);
            maxRetries = assistant.RequestMaxRetries;
            timeout = TimeSpan.FromSeconds(Math.Clamp(assistant.RequestTimeoutSeconds, 1, 24 * 60 * 60));
            schemaOptions = configuration.Schema switch
            {
                ModelProviderSchema.OpenAI => AssistantSnapshotMapper.Copy(assistant.OpenAIOptions),
                ModelProviderSchema.OpenAIResponses => AssistantSnapshotMapper.Copy(assistant.OpenAIResponsesOptions),
                ModelProviderSchema.Anthropic => AssistantSnapshotMapper.Copy(assistant.AnthropicOptions),
                ModelProviderSchema.Google => AssistantSnapshotMapper.Copy(assistant.GoogleOptions),
                ModelProviderSchema.Mistral => AssistantSnapshotMapper.Copy(assistant.MistralOptions),
                _ => null
            };
        }

        if (configuration.ModelId.IsNullOrWhiteSpace())
        {
            throw new HandledChatException.InvalidConfiguration(new InvalidOperationException("Model ID cannot be empty."));
        }

        var connection = configuration switch
        {
            OfficialAssistantConfiguration => ResolveOfficialConnection(configuration, timeout, maxRetries),
            _ => ResolveUserConnection(configuration, timeout, maxRetries)
        };

        try
        {
            return (connection.Schema, schemaOptions) switch
            {
                (ModelProviderSchema.OpenAI, OpenAIOptions options) => new OpenAIKernelMixin(
                    configuration,
                    options,
                    connection,
                    loggerFactory),
                (ModelProviderSchema.OpenAIResponses, OpenAIResponsesOptions options) => new OpenAIResponsesKernelMixin(
                    configuration,
                    options,
                    connection,
                    loggerFactory),
                (ModelProviderSchema.Anthropic, AnthropicOptions options) => new AnthropicKernelMixin(
                    configuration,
                    options,
                    connection),
                (ModelProviderSchema.Google, GoogleOptions options) => new GoogleKernelMixin(
                    configuration,
                    options,
                    connection,
                    loggerFactory),
                (ModelProviderSchema.Mistral, MistralOptions options) => new MistralKernelMixin(
                    configuration,
                    options,
                    connection,
                    loggerFactory),
                (ModelProviderSchema.Ollama, _) => new OllamaKernelMixin(
                    configuration,
                    connection),
                _ => throw new HandledChatException.InvalidConfiguration(
                    new NotSupportedException($"Model provider schema '{connection.Schema}' is not supported."),
                    new DynamicLocaleKey(LocaleKey.KernelMixinFactory_UnsupportedModelProviderSchema))
            };
        }
        catch
        {
            connection.HttpClient.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Resolves connection for Official (cloud gateway) mode.
    /// </summary>
    private ModelConnection ResolveOfficialConnection(AssistantConfiguration configuration, TimeSpan timeout, int maxRetries)
    {
        var schema = configuration.Schema;
        var endpoint = schema.NormalizeEndpoint(CloudConstants.AIGatewayBaseUrl) ??
            throw new HandledChatException.InvalidConfiguration.InvalidEndpoint(new InvalidOperationException("AI Gateway base URL is not configured."));

        // Official mode uses OAuth via the named HttpClient — no user API key needed.
        // Some SDKs require a non-null credential, so we pass null and let each mixin handle it
        // (e.g. OpenAIKernelMixin uses NoneAuthenticationPolicy, others use "official" placeholder).
        var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));
        httpClient.Timeout = timeout;

        return new ModelConnection(schema, endpoint, ApiKey: null, httpClient, EnrichOfficialExceptionEvidence, maxRetries);
    }

    /// <summary>
    /// Resolves connection for user-configured (non-Official) modes.
    /// </summary>
    private ModelConnection ResolveUserConnection(AssistantConfiguration configuration, TimeSpan timeout, int maxRetries)
    {
        if (!Uri.TryCreate(configuration.Endpoint, UriKind.Absolute, out _))
        {
            throw new HandledChatException.InvalidConfiguration.InvalidEndpoint(new InvalidOperationException("Invalid endpoint URL."));
        }

        var endpoint = configuration.Schema.NormalizeEndpoint(configuration.Endpoint)
            ?? throw new HandledChatException.InvalidConfiguration.InvalidEndpoint(new InvalidOperationException("Endpoint cannot be empty."));

        var apiKey = ApiKey.GetKey(configuration.ApiKey);

        // Create an HttpClient instance using the factory.
        // It will have the configured settings (timeout and proxy).
        var httpClient = httpClientFactory.CreateClient();
        httpClient.Timeout = timeout;

        return new ModelConnection(configuration.Schema, endpoint, apiKey, httpClient, null, maxRetries);
    }

    private static void EnrichOfficialExceptionEvidence(ChatExceptionEvidence evidence)
    {
        if (evidence.ResponseBody is not { } body) return;
        try
        {
            var payload = JsonSerializer.Deserialize(body, ApiPayloadJsonSerializerContext.Default.ApiPayload);
            if (payload is not { Success: false, Error: { } error }) return;
            if (error.Upstream is { } upstream)
            {
                evidence.GatewayStatusCode = evidence.StatusCode;
                evidence.StatusCode = upstream.StatusCode;
                using var upstreamBody = upstream.Body;
                evidence.ErrorCode = null;
                evidence.ErrorType = null;
                evidence.Parameter = null;
                evidence.ErrorMessage = null;
                ChatExceptionEvidenceExtractor.ExtractResponseBody(evidence, upstreamBody?.RootElement.GetRawText());
                return;
            }

            evidence.ConnectionErrorCode = error.Code;
            evidence.FriendlyMessageKey = ParseOfficialErrorCode(error.Code);
            // Keep gateway text as diagnostic evidence rather than appending it to the UI key.
            evidence.ErrorMessage = error.Message;
        }
        catch (JsonException)
        {
            // Non-gateway and malformed response bodies retain their SDK/HTTP evidence.
        }
    }

    private static DynamicLocaleKey? ParseOfficialErrorCode(string? errorCode) => errorCode?.ToLowerInvariant() switch
    {
        "validation_error" or "invalid_request" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_InvalidRequest),
        "invalid_model" or "model_not_found" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_ModelUnavailable),
        "not_found" =>
            new DynamicLocaleKey(LocaleKey.FriendlyExceptionMessage_HttpRequest_NotFound),

        "auth_missing" or "auth_invalid" or "auth_expired" or "auth_insufficient_scope" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_Authentication),

        "billing_insufficient_credits" or "billing_insufficient_tier" =>
            new DynamicLocaleKey(LocaleKey.HandledChatException_QuotaExceeded),
        "billing_user_not_found" or "user_not_found" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_BillingAccountNotFound),
        "billing_user_banned" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_AccountSuspended),

        "request_entity_too_large" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_RequestTooLarge),

        "upstream_timeout" =>
            new DynamicLocaleKey(LocaleKey.HandledChatException_Timeout),
        "upstream_unavailable" or "internal_error" or "config_error" or "deduct_credits" =>
            new DynamicLocaleKey(LocaleKey.HandledChatException_ServiceUnavailable),

        "rate_limit_api" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_RateLimitApi),
        "rate_limit_llm_burst" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_RateLimitLlmBurst),
        "rate_limit_llm_2h" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_RateLimitLlm2h),
        "rate_limit_llm_24h" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_RateLimitLlm24h),
        "rate_limit_expensive_model_5h" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_5HourQuotaLimitExceeded),
        "rate_limit_expensive_model_7d" =>
            new DynamicLocaleKey(LocaleKey.KernelMixinFactory_OfficialError_7DayQuotaLimitExceeded),

        _ => null
    };
}
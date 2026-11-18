using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Everywhere.Harness;
using Everywhere.Harness.Providers.Mistral;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Everywhere.AI.Tests;

public enum Provider
{
    OpenAI,
    OpenAIResponses,
    Anthropic,
    Gemini,
    GeminiInteractions,
    Mistral,
    Ollama
}

public static class ProviderExtensions
{
    // Exercise the same evidence adapter as official sessions without a deployed gateway or credentials.
    internal static ModelSession CreateOpenAISession(HttpClient client, string modelId, bool isOfficial)
    {
        Action<ChatExceptionEvidence>? enrich = null;
        if (isOfficial)
        {
            var method = typeof(ModelSessionFactory).GetMethod("EnrichOfficialExceptionEvidence", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?? throw new MissingMethodException("Official evidence adapter is missing.");
            enrich = method.CreateDelegate<Action<ChatExceptionEvidence>>();
        }

        var endpoint = client.BaseAddress?.AbsoluteUri ?? throw new InvalidOperationException("Missing test endpoint.");
        var connection = new ModelConnection(ModelProviderSchema.OpenAI, endpoint, isOfficial ? null : "test-key", client, enrich, 1);
        return new OpenAIModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.OpenAI, ModelId = modelId },
            new OpenAIOptions(),
            connection,
            NullLoggerFactory.Instance);
    }

    // Raw SDK tests choose their own budgets. Production sessions disable SDK transient
    // retries because StreamRequestAsync owns the application budget.
    public static IChatClient CreateClient(this Provider provider, HttpClient client, int maxRetries = 0) => provider switch
    {
        Provider.OpenAI => new ChatClient("test-model", new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = client.BaseAddress,
            Transport = new HttpClientPipelineTransport(client),
            RetryPolicy = new ClientRetryPolicy(maxRetries)
        }).AsIChatClient(),
        Provider.OpenAIResponses => CreateResponsesClient(client, maxRetries),
        Provider.Anthropic => new AnthropicClient(new ClientOptions
        {
            ApiKey = "test-key",
            BaseUrl = client.BaseAddress?.AbsoluteUri ?? throw new InvalidOperationException("The test endpoint is missing."),
            HttpClient = client,
            Timeout = client.Timeout,
            MaxRetries = maxRetries
        }).AsIChatClient("test-model", 1024),
        Provider.Gemini => new GoogleModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.Google, ModelId = "test-model" },
            new GoogleOptions(),
            new ModelConnection(
                ModelProviderSchema.Google,
                ModelProviderSchema.Google.NormalizeEndpoint(client.BaseAddress?.AbsoluteUri) ?? throw new InvalidOperationException("Missing test endpoint."),
                "test-key",
                client,
                null)).ChatClient,
        Provider.GeminiInteractions => new GoogleInteractionsModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.GoogleInteractions, ModelId = "test-model" },
            new GoogleInteractionsOptions(),
            new ModelConnection(
                ModelProviderSchema.GoogleInteractions,
                ModelProviderSchema.GoogleInteractions.NormalizeEndpoint(client.BaseAddress?.AbsoluteUri) ?? throw new InvalidOperationException("Missing test endpoint."),
                "test-key",
                client,
                null)).ChatClient,
        Provider.Mistral => new MistralChatClient(
            client,
            new Uri(client.BaseAddress ?? throw new InvalidOperationException("Missing test endpoint."), "chat/completions"),
            "test-model",
            "test-key"),
        Provider.Ollama => new OllamaApiClient(client, "test-model"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static IChatClient CreateResponsesClient(HttpClient client, int maxRetries = 0) =>
        new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = client.BaseAddress,
            Transport = new HttpClientPipelineTransport(client),
            RetryPolicy = new ClientRetryPolicy(maxRetries)
        }).AsIChatClient("test-model");
}

using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Everywhere.Harness.Providers.Mistral;
using Microsoft.Extensions.AI;
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
    Mistral,
    Ollama
}

public static class ProviderExtensions
{
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
        Provider.Gemini => new GeminiModelSession(
            new AdvancedAssistantConfiguration { Schema = ModelProviderSchema.Google, ModelId = "test-model" },
            new GoogleOptions(),
            new ModelConnection(
                ModelProviderSchema.Google,
                ModelProviderSchema.Google.NormalizeEndpoint(client.BaseAddress?.AbsoluteUri) ?? throw new InvalidOperationException("Missing test endpoint."),
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

using System.ClientModel;
using System.ClientModel.Primitives;
using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;
using Microsoft.SemanticKernel.Connectors.MistralAI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Everywhere.AI.Tests;

public enum Provider
{
    OpenAI, OpenAIResponses, Anthropic, Gemini, Mistral, Ollama
}

public static class ProviderExtensions
{
    // Raw SDK tests choose their own budgets. Production mixins disable SDK transient
    // retries because StreamRequestAsync owns the application budget.
    public static IChatCompletionService CreateService(this Provider provider, HttpClient client, int maxRetries = 0) => provider switch
    {
        Provider.OpenAI => new ChatClient("test-model", new ApiKeyCredential("test-key"), new OpenAIClientOptions
        {
            Endpoint = client.BaseAddress, Transport = new HttpClientPipelineTransport(client), RetryPolicy = new ClientRetryPolicy(maxRetries)
        }).AsIChatClient().AsChatCompletionService(),
        Provider.OpenAIResponses => CreateResponsesClient(client, maxRetries).AsChatCompletionService(),
        Provider.Anthropic => new AnthropicClient(new ClientOptions
        {
            ApiKey = "test-key", BaseUrl = client.BaseAddress?.AbsoluteUri ?? throw new InvalidOperationException("The test endpoint is missing."), HttpClient = client,
            Timeout = client.Timeout, MaxRetries = maxRetries
        }).AsIChatClient("test-model", 1024).AsChatCompletionService(),
        Provider.Gemini => new GoogleAIGeminiChatCompletionService("test-model", "test-key", httpClient: client, customEndpoint: client.BaseAddress),
        Provider.Mistral => new MistralAIChatCompletionService("test-model", "test-key", endpoint: client.BaseAddress, httpClient: client),
        Provider.Ollama => new OllamaApiClient(client, "test-model").AsChatCompletionService(),
        _ => throw new ArgumentOutOfRangeException(nameof(provider))
    };

    public static IChatClient CreateResponsesClient(HttpClient client, int maxRetries = 0) =>
        new ResponsesClient(new ApiKeyCredential("test-key"), new ResponsesClientOptions
        {
            Endpoint = client.BaseAddress, Transport = new HttpClientPipelineTransport(client), RetryPolicy = new ClientRetryPolicy(maxRetries)
        }).AsIChatClient("test-model");
}


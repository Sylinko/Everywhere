using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.MistralAI;
using Microsoft.SemanticKernel.Connectors.MistralAI.Client;
using UsageDetails = Microsoft.Extensions.AI.UsageDetails;

namespace Everywhere.Core.Tests.AI;

public class MistralIntegrationTests
{
    [Test]
    public void CustomAssistant_WhenSerialized_DoesNotIncludeIsMistral()
    {
        var assistant = new CustomAssistant
        {
            Schema = ModelProviderSchema.Mistral
        };

        var json = JsonSerializer.Serialize(assistant);
        using var document = JsonDocument.Parse(json);

        Assert.That(document.RootElement.TryGetProperty(nameof(Assistant.IsMistral), out _), Is.False);
    }

    [Test]
    [NonParallelizable]
    public void GetPromptExecutionSettings_InCommaDecimalCulture_ParsesInvariantSamplingValues()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var assistant = new CustomAssistant
            {
                Schema = ModelProviderSchema.Mistral,
                ModelId = "mistral-small-latest"
            };
            assistant.MistralOptions.Temperature = "0.25";
            assistant.MistralOptions.TopP = "0.75";
            using var httpClient = new HttpClient();
            var connection = new ModelConnection(
                ModelProviderSchema.Mistral,
                "https://example.com/v1",
                "test-key",
                httpClient,
                null);
            using var mixin = new MistralKernelMixin(assistant, connection, NullLoggerFactory.Instance);

            var settings = (MistralAIPromptExecutionSettings)mixin.GetPromptExecutionSettings();

            Assert.Multiple(() =>
            {
                Assert.That(settings.Temperature, Is.EqualTo(0.25));
                Assert.That(settings.TopP, Is.EqualTo(0.75));
            });
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Test]
    public void GetPromptExecutionSettings_WithDefaultReasoningOptions_OmitsReasoningEffort()
    {
        var assistant = new CustomAssistant
        {
            Schema = ModelProviderSchema.Mistral,
            ModelId = "mistral-large-latest"
        };
        using var httpClient = new HttpClient();
        var connection = new ModelConnection(
            ModelProviderSchema.Mistral,
            "https://example.com/v1",
            "test-key",
            httpClient,
            null);
        using var mixin = new MistralKernelMixin(assistant, connection, NullLoggerFactory.Instance);

        var settings = (MistralAIPromptExecutionSettings)mixin.GetPromptExecutionSettings();

        Assert.That(settings.ExtensionData?.ContainsKey("reasoning_effort") ?? false, Is.False);
    }

    [Test]
    public void GetPromptExecutionSettings_WithCustomReasoningEffort_TrimsAndPassesThroughValue()
    {
        var assistant = new CustomAssistant
        {
            Schema = ModelProviderSchema.Mistral,
            ModelId = "mistral-small-latest"
        };
        assistant.MistralOptions.ReasoningEffort = " future-level ";
        using var httpClient = new HttpClient();
        var connection = new ModelConnection(
            ModelProviderSchema.Mistral,
            "https://example.com/v1",
            "test-key",
            httpClient,
            null);
        using var mixin = new MistralKernelMixin(assistant, connection, NullLoggerFactory.Instance);

        var settings = (MistralAIPromptExecutionSettings)mixin.GetPromptExecutionSettings();

        Assert.That(settings.ExtensionData?["reasoning_effort"], Is.EqualTo("future-level"));
    }

    [Test]
    public void GetPromptExecutionSettings_WhenReasoningIsDisabled_SendsNone()
    {
        var assistant = new CustomAssistant
        {
            Schema = ModelProviderSchema.Mistral,
            ModelId = "mistral-small-latest"
        };
        assistant.MistralOptions.IncludeReasoningContent = false;
        assistant.MistralOptions.ReasoningEffort = "high";
        using var httpClient = new HttpClient();
        var connection = new ModelConnection(
            ModelProviderSchema.Mistral,
            "https://example.com/v1",
            "test-key",
            httpClient,
            null);
        using var mixin = new MistralKernelMixin(assistant, connection, NullLoggerFactory.Instance);

        var settings = (MistralAIPromptExecutionSettings)mixin.GetPromptExecutionSettings();

        Assert.That(settings.ExtensionData?["reasoning_effort"], Is.EqualTo("none"));
    }

    [Test]
    public async Task GetChatMessageContentsAsync_WithBlankStringReasoningEffort_OmitsParameter()
    {
        using var request = await SendRequestWithReasoningEffortAsync(" \t ");

        Assert.That(request.RootElement.TryGetProperty("reasoning_effort", out _), Is.False);
    }

    [Test]
    public async Task GetChatMessageContentsAsync_WithJsonReasoningEffort_TrimsAndPassesThroughValue()
    {
        using var valueDocument = JsonDocument.Parse("\" future-level \"");
        using var request = await SendRequestWithReasoningEffortAsync(valueDocument.RootElement.Clone());

        Assert.That(request.RootElement.GetProperty("reasoning_effort").GetString(), Is.EqualTo("future-level"));
    }

    [Test]
    public async Task GetChatMessageContentsAsync_WithReasoningHistory_ReplaysThinkingBeforeText()
    {
        var handler = new SingleResponseMistralHandler();
        using var httpClient = new HttpClient(handler);
        var service = new MistralAIChatCompletionService(
            "mistral-small-latest",
            "test-key",
            new Uri("https://example.com/v1"),
            httpClient,
            NullLoggerFactory.Instance);
        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.User, "What is 17 * 23?"),
            new ChatMessageContent(
                AuthorRole.Assistant,
                [
#pragma warning disable SKEXP0110 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
                    new ReasoningContent("17 * 20 is 340, and 17 * 3 is 51."),
#pragma warning restore SKEXP0110
                    new TextContent("391")
                ]),
            new ChatMessageContent(AuthorRole.User, "Now multiply that by 3.")
        };

        await service.GetChatMessageContentsAsync(chatHistory);

        using var request = JsonDocument.Parse(handler.RequestBody!);
        AssertReasoningContent(
            request.RootElement.GetProperty("messages")[1],
            "17 * 20 is 340, and 17 * 3 is 51.",
            "391");
    }

    [Test]
    public async Task GetChatMessageContentsAsync_WhenAutoInvokingTool_ReplaysReasoningAndRemovesToolsAtMaximum()
    {
        var handler = new SequentialMistralHandler();
        using var httpClient = new HttpClient(handler);
        var service = new MistralAIChatCompletionService(
            "mistral-small-latest",
            "test-key",
            new Uri("https://example.com/v1"),
            httpClient,
            NullLoggerFactory.Instance);
        var kernel = new Kernel();
        var plugin = kernel.Plugins.AddFromType<WeatherPlugin>();
        var settings = new MistralAIPromptExecutionSettings
        {
            ToolCallBehavior = MistralAIToolCallBehavior.RequiredFunctions(plugin, autoInvoke: true)
        };
        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.User, "What is the weather?")
        };

        await service.GetChatMessageContentsAsync(chatHistory, settings, kernel);

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
        using var firstRequest = JsonDocument.Parse(handler.RequestBodies[0]);
        using var finalRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        var replayedAssistantMessage = finalRequest.RootElement.GetProperty("messages")[1];
        Assert.Multiple(() =>
        {
            Assert.That(firstRequest.RootElement.TryGetProperty("tools", out _), Is.True);
            Assert.That(firstRequest.RootElement.GetProperty("tool_choice").GetString(), Is.EqualTo("any"));
            Assert.That(finalRequest.RootElement.TryGetProperty("tools", out _), Is.False);
            Assert.That(finalRequest.RootElement.TryGetProperty("tool_choice", out _), Is.False);
            Assert.That(replayedAssistantMessage.GetProperty("tool_calls").GetArrayLength(), Is.EqualTo(1));
        });
        AssertReasoningContent(replayedAssistantMessage, "I should check the weather.", "I'll check.");
    }

    [Test]
    public async Task GetStreamingChatMessageContentsAsync_WhenAutoInvokingTool_ReplaysStreamedReasoning()
    {
        var handler = new StreamingSequentialMistralHandler();
        using var httpClient = new HttpClient(handler);
        var service = new MistralAIChatCompletionService(
            "mistral-small-latest",
            "test-key",
            new Uri("https://example.com/v1"),
            httpClient,
            NullLoggerFactory.Instance);
        var kernel = new Kernel();
        kernel.Plugins.AddFromType<WeatherPlugin>();
        var settings = new MistralAIPromptExecutionSettings
        {
            ToolCallBehavior = MistralAIToolCallBehavior.AutoInvokeKernelFunctions
        };
        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.User, "What is the weather?")
        };

        var usageUpdates = new List<MistralUsage>();
        await foreach (var content in service.GetStreamingChatMessageContentsAsync(chatHistory, settings, kernel))
        {
            if (content.Metadata?.TryGetValue("Usage", out var usage) is true && usage is MistralUsage mistralUsage)
            {
                usageUpdates.Add(mistralUsage);
            }
        }

        Assert.That(handler.RequestBodies, Has.Count.EqualTo(2));
        using var secondRequest = JsonDocument.Parse(handler.RequestBodies[1]);
        var replayedAssistantMessage = secondRequest.RootElement.GetProperty("messages")[1];
        Assert.That(replayedAssistantMessage.GetProperty("tool_calls").GetArrayLength(), Is.EqualTo(1));
        AssertReasoningContent(replayedAssistantMessage, "I should check the weather.", "I'll check.");
        Assert.That(usageUpdates, Has.Count.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(usageUpdates[0].TotalTokens, Is.EqualTo(2));
            Assert.That(usageUpdates[1].TotalTokens, Is.EqualTo(5));
        });
    }

    [Test]
    public async Task GetStreamingChatMessageContentsAsync_WhenTerminalChunkContainsOnlyUsage_ConvertsUsageMetadata()
    {
        var handler = new StreamingUsageMistralHandler();
        using var httpClient = new HttpClient(handler);
        var assistant = new CustomAssistant
        {
            Schema = ModelProviderSchema.Mistral,
            ModelId = "mistral-small-latest"
        };
        var connection = new ModelConnection(
            ModelProviderSchema.Mistral,
            "https://example.com/v1",
            "test-key",
            httpClient,
            null);
        using var mixin = new MistralKernelMixin(assistant, connection, NullLoggerFactory.Instance);
        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.User, "Hello")
        };

        var updates = new List<StreamingChatMessageContent>();
        await foreach (var content in mixin.ChatCompletionService.GetStreamingChatMessageContentsAsync(chatHistory))
        {
            updates.Add(content);
        }

        Assert.That(updates, Has.Count.EqualTo(2));
        var usage = updates
            .Select(content => content.Metadata?.TryGetValue("Usage", out var value) is true ? value : null)
            .OfType<UsageDetails>()
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(usage.InputTokenCount, Is.EqualTo(3));
            Assert.That(usage.OutputTokenCount, Is.EqualTo(2));
            Assert.That(usage.TotalTokenCount, Is.EqualTo(5));
        });
    }

    private static void AssertReasoningContent(
        JsonElement assistantMessage,
        string expectedReasoning,
        string expectedText)
    {
        var content = assistantMessage.GetProperty("content");
        var thinking = content[0];
        var text = content[1];

        Assert.Multiple(() =>
        {
            Assert.That(content.ValueKind, Is.EqualTo(JsonValueKind.Array));
            Assert.That(content.GetArrayLength(), Is.EqualTo(2));
            Assert.That(thinking.GetProperty("type").GetString(), Is.EqualTo("thinking"));
            Assert.That(thinking.GetProperty("thinking")[0].GetProperty("type").GetString(), Is.EqualTo("text"));
            Assert.That(thinking.GetProperty("thinking")[0].GetProperty("text").GetString(), Is.EqualTo(expectedReasoning));
            Assert.That(text.GetProperty("type").GetString(), Is.EqualTo("text"));
            Assert.That(text.GetProperty("text").GetString(), Is.EqualTo(expectedText));
        });
    }

    private static async Task<JsonDocument> SendRequestWithReasoningEffortAsync(object reasoningEffort)
    {
        var handler = new SingleResponseMistralHandler();
        using var httpClient = new HttpClient(handler);
        var service = new MistralAIChatCompletionService(
            "mistral-small-latest",
            "test-key",
            new Uri("https://example.com/v1"),
            httpClient,
            NullLoggerFactory.Instance);
        var settings = new MistralAIPromptExecutionSettings
        {
            ExtensionData = new Dictionary<string, object>
            {
                ["reasoning_effort"] = reasoningEffort
            }
        };
        var chatHistory = new ChatHistory
        {
            new ChatMessageContent(AuthorRole.User, "Hello")
        };

        await service.GetChatMessageContentsAsync(chatHistory, settings);

        return JsonDocument.Parse(handler.RequestBody!);
    }

    private sealed class SingleResponseMistralHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            const string ResponseJson = """
                {
                  "id": "final",
                  "object": "chat.completion",
                  "created": 1,
                  "model": "mistral-small-latest",
                  "choices": [{
                    "index": 0,
                    "message": { "role": "assistant", "content": "1173" },
                    "finish_reason": "stop"
                  }],
                  "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                }
                """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SequentialMistralHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var responseJson = RequestBodies.Count == 1
                ? """
                  {
                    "id": "first",
                    "object": "chat.completion",
                    "created": 1,
                    "model": "mistral-small-latest",
                    "choices": [{
                      "index": 0,
                      "message": {
                        "role": "assistant",
                        "content": [
                          {
                            "type": "thinking",
                            "thinking": [{ "type": "text", "text": "I should check the weather." }]
                          },
                          { "type": "text", "text": "I'll check." }
                        ],
                        "tool_calls": [{
                          "id": "call-1",
                          "function": {
                            "name": "WeatherPlugin-GetWeather",
                            "arguments": "{}"
                          }
                        }]
                      },
                      "finish_reason": "tool_calls"
                    }],
                    "usage": { "prompt_tokens": 1, "completion_tokens": 1, "total_tokens": 2 }
                  }
                  """
                : """
                  {
                    "id": "final",
                    "object": "chat.completion",
                    "created": 2,
                    "model": "mistral-small-latest",
                    "choices": [{
                      "index": 0,
                      "message": { "role": "assistant", "content": "Sunny." },
                      "finish_reason": "stop"
                    }],
                    "usage": { "prompt_tokens": 2, "completion_tokens": 1, "total_tokens": 3 }
                  }
                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StreamingSequentialMistralHandler : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var responseBody = RequestBodies.Count == 1
                ? """
                  data: {"id":"first","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"role":"assistant","content":""},"finish_reason":null}]}

                  data: {"id":"first","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":[{"type":"thinking","thinking":[{"type":"text","text":"I should check the weather."}]}]},"finish_reason":null}]}

                  data: {"id":"first","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":"I'll check."},"finish_reason":null}]}

                  data: {"id":"first","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"content":null,"tool_calls":[{"id":"call-1","function":{"name":"WeatherPlugin-GetWeather","arguments":"{}"}}]},"finish_reason":"tool_calls"}]}

                  data: {"id":"first","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}

                  data: [DONE]

                  """
                : """
                  data: {"id":"final","object":"chat.completion.chunk","created":2,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"role":"assistant","content":"Sunny."},"finish_reason":"stop"}]}

                  data: {"id":"final","object":"chat.completion.chunk","created":2,"model":"mistral-small-latest","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

                  data: [DONE]

                  """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private sealed class StreamingUsageMistralHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            const string ResponseBody = """
                data: {"id":"response","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[{"index":0,"delta":{"role":"assistant","content":"Hello!"},"finish_reason":"stop"}]}

                data: {"id":"response","object":"chat.completion.chunk","created":1,"model":"mistral-small-latest","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

                data: [DONE]

                """;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody, Encoding.UTF8, "text/event-stream")
            });
        }
    }

    private sealed class WeatherPlugin
    {
        [KernelFunction]
        public static string GetWeather() => "Sunny.";
    }
}
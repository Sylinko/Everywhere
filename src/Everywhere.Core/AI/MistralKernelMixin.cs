using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.MistralAI;
using Microsoft.SemanticKernel.Connectors.MistralAI.Client;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Mistral AI models.
/// Uses the Semantic Kernel MistralAI connector with extensions for deep thinking and usage tracking.
/// </summary>
public sealed class MistralKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly MistralOptions _options;

    public MistralKernelMixin(
        Assistant assistant,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(assistant, connection)
    {
        _options = assistant.MistralOptions;

        var service = new MistralAIChatCompletionService(
            modelId: ModelId,
            apiKey: ApiKey ?? "NO_API_KEY",
            endpoint: new Uri(Endpoint, UriKind.Absolute),
            httpClient: connection.HttpClient,
            loggerFactory: loggerFactory,
            skipHttpClientProvider: true);

        ChatCompletionService = new OptimizedMistralChatCompletionService(service);
    }

    public override bool IsPersistentMessageMetadataKey(string key) => key is "reasoningSignature";

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null)
    {
        // Convert FunctionChoiceBehavior to MistralAIToolCallBehavior
        MistralAIToolCallBehavior? toolCallBehavior = null;
        if (functionChoiceBehavior is not null and not NoneFunctionChoiceBehavior)
        {
            toolCallBehavior = MistralAIToolCallBehavior.EnableKernelFunctions;
        }

        var settings = new MistralAIPromptExecutionSettings
        {
            Temperature = double.TryParse(_options.Temperature, out var temperature) ? temperature : 0.7,
            TopP = double.TryParse(_options.TopP, out var topP) ? topP : 1,
            ToolCallBehavior = toolCallBehavior,
        };

        // https://docs.mistral.ai/capabilities/reasoning/
        if (_options.IncludeReasoningContent)
        {
            settings.ExtensionData = new Dictionary<string, object>
            {
                ["thinking"] = new { type = "enabled" }
            };
        }

        return settings;
    }

    /// <summary>
    /// Wrapper around MistralAI's IChatCompletionService to inject Usage metadata
    /// into streaming responses. Reasoning content is already handled by the patched MistralClient.
    /// </summary>
    private sealed class OptimizedMistralChatCompletionService(IChatCompletionService innerService) : IChatCompletionService
    {
        public IReadOnlyDictionary<string, object?> Attributes => innerService.Attributes;

        public Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            return innerService.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
        }

        public async IAsyncEnumerable<StreamingChatMessageContent> GetStreamingChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var content in innerService.GetStreamingChatMessageContentsAsync(
                               chatHistory,
                               executionSettings,
                               kernel,
                               cancellationToken))
            {
                // Inject Usage metadata for consistent handling in ChatService
                if (content.Metadata?.TryGetValue("Usage", out var usageObj) is true &&
                    usageObj is MistralUsage usage)
                {
                    var usageDetails = new UsageDetails
                    {
                        InputTokenCount = usage.PromptTokens,
                        OutputTokenCount = usage.CompletionTokens,
                        TotalTokenCount = usage.TotalTokens
                    };

                    var newMetadata = new Dictionary<string, object?>();
                    if (content.Metadata is not null)
                    {
                        foreach (var (key, value) in content.Metadata)
                        {
                            newMetadata[key] = value;
                        }
                    }
                    newMetadata["Usage"] = usageDetails;

                    yield return new StreamingChatMessageContent(
                        content.Role,
                        content.Content,
                        content.InnerContent,
                        content.ChoiceIndex,
                        content.ModelId,
                        content.Encoding,
                        newMetadata)
                    {
                        Items = content.Items
                    };
                    continue;
                }

                yield return content;
            }
        }
    }
}

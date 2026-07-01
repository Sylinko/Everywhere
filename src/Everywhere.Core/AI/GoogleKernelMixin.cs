using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Google;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Google Gemini models.
/// </summary>
public sealed class GoogleKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly GoogleOptions _options;

    public GoogleKernelMixin(
        Assistant assistant,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(assistant, connection)
    {
        _options = assistant.GoogleOptions;

        var service = new GoogleAIGeminiChatCompletionService(
            ModelId,
            ApiKey ?? "NO_API_KEY",
            httpClient: connection.HttpClient,
            loggerFactory: loggerFactory,
            customEndpoint: new Uri(Endpoint, UriKind.Absolute));
        ChatCompletionService = new OptimizedGeminiChatCompletionService(service);
    }

    public override bool IsPersistentMessageMetadataKey(string key) => key is "thoughtSignature";

    public override PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null)
    {
        // Convert FunctionChoiceBehavior to GeminiToolCallBehavior
        GeminiToolCallBehavior? toolCallBehavior = null;
        if (functionChoiceBehavior is not null and not NoneFunctionChoiceBehavior)
        {
            toolCallBehavior = GeminiToolCallBehavior.EnableKernelFunctions; // should deal with AutoInvoke, but not used in Everywhere afaik
        }

        return new GeminiPromptExecutionSettings
        {
            Temperature = double.TryParse(_options.Temperature, out var temperature) ? temperature : null,
            TopP = double.TryParse(_options.TopP, out var topP) ? topP : null,
            TopK = int.TryParse(_options.TopK, out var topK) ? topK : null,
            ToolCallBehavior = toolCallBehavior,
            ThinkingConfig = GetThinkingConfig()
        };

        // https://ai.google.dev/gemini-api/docs/thinking
        GeminiThinkingConfig? GetThinkingConfig()
        {
            if (!_options.IncludeThoughts) return null;

            var thinkingConfig = new GeminiThinkingConfig
            {
                IncludeThoughts = true
            };

            if (int.TryParse(_options.ThinkingBudget, out var thinkingBudget)) thinkingConfig.ThinkingBudget = thinkingBudget;
            thinkingConfig.ThinkingLevel = _options.ThinkingLevel;
            return thinkingConfig;
        }
    }

    /// <summary>
    /// Wrapper around Google Gemini's IChatCompletionService to inject Usage metadata.
    /// The underlying semantic-kernel Gemini connector now supports FunctionCallContent/FunctionResultContent natively.
    /// </summary>
    private sealed class OptimizedGeminiChatCompletionService(IChatCompletionService innerService) : IChatCompletionService
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
                // inject GeminiMetadata into "Usage" key for consistent handling in ChatService
                if (content.Metadata is GeminiMetadata geminiMetadata)
                {
                    var usageDetails = new UsageDetails
                    {
                        InputTokenCount = geminiMetadata.PromptTokenCount,
                        OutputTokenCount = geminiMetadata.CandidatesTokenCount + geminiMetadata.ThoughtsTokenCount,
                        TotalTokenCount = geminiMetadata.TotalTokenCount
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
                }
                else
                {
                    yield return content;
                }
            }
        }
    }
}
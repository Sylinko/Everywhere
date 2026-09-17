using System.Globalization;
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
    /// <inheritdoc/>
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly MistralOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="MistralKernelMixin"/> class.
    /// </summary>
    /// <param name="assistant">The assistant that owns the Mistral configuration.</param>
    /// <param name="connection">The model connection used to access the Mistral API.</param>
    /// <param name="loggerFactory">The logger factory used by the Mistral connector.</param>
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
            loggerFactory: loggerFactory);

        ChatCompletionService = new OptimizedMistralChatCompletionService(service);
    }

    /// <inheritdoc/>
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
            Temperature = double.TryParse(_options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : 0.7,
            TopP = double.TryParse(_options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : 1,
            ToolCallBehavior = toolCallBehavior,
        };

        // https://docs.mistral.ai/capabilities/reasoning/
        var reasoningEffort = _options.IncludeReasoningContent ? _options.ReasoningEffort : "none";
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            settings.ExtensionData = new Dictionary<string, object>
            {
                // Keep non-empty values open-ended for model-specific and future effort levels.
                ["reasoning_effort"] = reasoningEffort.Trim()
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

        public async Task<IReadOnlyList<ChatMessageContent>> GetChatMessageContentsAsync(
            ChatHistory chatHistory,
            PromptExecutionSettings? executionSettings = null,
            Kernel? kernel = null,
            CancellationToken cancellationToken = default)
        {
            var contents = await innerService.GetChatMessageContentsAsync(chatHistory, executionSettings, kernel, cancellationToken);
            return contents.Select(ConvertUsageMetadata).ToArray();
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
                yield return ConvertUsageMetadata(content);
            }
        }

        private static ChatMessageContent ConvertUsageMetadata(ChatMessageContent content)
        {
            if (!TryConvertUsageMetadata(content.Metadata, out var metadata))
            {
                return content;
            }

            return new ChatMessageContent(
                content.Role,
                content.Content,
                content.ModelId,
                content.InnerContent,
                content.Encoding,
                metadata)
            {
                AuthorName = content.AuthorName,
                Items = content.Items
            };
        }

        private static StreamingChatMessageContent ConvertUsageMetadata(StreamingChatMessageContent content)
        {
            if (!TryConvertUsageMetadata(content.Metadata, out var metadata))
            {
                return content;
            }

            return new StreamingChatMessageContent(
                content.Role,
                content.Content,
                content.InnerContent,
                content.ChoiceIndex,
                content.ModelId,
                content.Encoding,
                metadata)
            {
                AuthorName = content.AuthorName,
                Items = content.Items
            };
        }

        private static bool TryConvertUsageMetadata(
            IReadOnlyDictionary<string, object?>? source,
            out IReadOnlyDictionary<string, object?> metadata)
        {
            if (source?.TryGetValue("Usage", out var usageObj) is not true || usageObj is not MistralUsage usage)
            {
                metadata = source ?? new Dictionary<string, object?>();
                return false;
            }

            var converted = new Dictionary<string, object?>(source)
            {
                ["Usage"] = new UsageDetails
                {
                    InputTokenCount = usage.PromptTokens,
                    OutputTokenCount = usage.CompletionTokens,
                    TotalTokenCount = usage.TotalTokens
                }
            };
            metadata = converted;
            return true;
        }
    }
}
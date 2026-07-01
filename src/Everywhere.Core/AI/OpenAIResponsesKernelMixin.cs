using System.ClientModel;
using System.ClientModel.Primitives;
using Everywhere.Common;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Responses;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for OpenAI models via Responses API.
/// </summary>
public sealed class OpenAIResponsesKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OpenAIResponsesOptions _options;

    public OpenAIResponsesKernelMixin(
        Assistant assistant,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(assistant, connection)
    {
        _options = assistant.OpenAIResponsesOptions;

        ChatCompletionService = new OptimizedChatClient(
            new ResponsesClient(
                new ApiKeyCredential(ApiKey ?? "NO_API_KEY"),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    Transport = new HttpClientPipelineTransport(connection.HttpClient, true, loggerFactory)
                }
            ).AsIChatClient(ModelId),
            this
        ).AsChatCompletionService();
    }

    /// <summary>
    /// optimized wrapper around OpenAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedChatClient(IChatClient originalClient, OpenAIResponsesKernelMixin owner) : DelegatingChatClient(originalClient)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // MEAI not supporting Deep Thinking will skip adding the reasoning options
            // This is a workaround
            options ??= new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            // cache the value to avoid property changes during enumeration
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                // Ensure that all FunctionCallContent items have a unique CallId.
                for (var i = 0; i < update.Contents.Count; i++)
                {
                    var content = update.Contents[i];
                    update.Contents[i] = content switch
                    {
                        FunctionCallContent { Name.Length: > 0, CallId: null or { Length: 0 } } missingIdContent =>
                            // Generate a unique ToolCallId for the function call update.
                            new FunctionCallContent(Guid.CreateVersion7().ToString("N"), missingIdContent.Name, missingIdContent.Arguments),
                        ErrorContent errorContent => throw HandledChatException.FromErrorCode(
                            new Exception(errorContent.Message),
                            errorContent.ErrorCode ?? errorContent.Message),
                        _ => update.Contents[i]
                    };
                }

                yield return update;
            }
        }

        private CreateResponseOptions RawRepresentationFactory(IChatClient _)
        {
            var options = owner._options;
            var reasoningEffortLevel = options.ReasoningEffort switch
            {
                { Length: > 0 } => new ResponseReasoningEffortLevel(options.ReasoningEffort),
                _ => (ResponseReasoningEffortLevel?)null
            };
            var reasoningSummaryVerbosity = options.ReasoningSummary switch
            {
                { Length: > 0 } => new ResponseReasoningSummaryVerbosity(options.ReasoningSummary),
                _ => (ResponseReasoningSummaryVerbosity?)null
            };
            return new CreateResponseOptions
            {
                Temperature = float.TryParse(options.Temperature, out var temperature) ? temperature : null,
                TopP = float.TryParse(options.TopP, out var topP) ? topP : null,
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = reasoningEffortLevel,
                    ReasoningSummaryVerbosity = reasoningSummaryVerbosity
                }
            };
        }
    }
}
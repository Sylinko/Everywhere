using System.ClientModel;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI.Responses;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for OpenAI models via Responses API.
/// </summary>
public sealed class OpenAIResponsesKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OpenAIResponsesOptions _options;

    public OpenAIResponsesKernelMixin(
        AssistantConfiguration configuration,
        OpenAIResponsesOptions options,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(configuration, connection)
    {
        _options = options;

        ChatCompletionService = new OptimizedChatClient(
            new ResponsesClient(
                new ApiKeyCredential(ApiKey ?? "NO_API_KEY"),
                new ResponsesClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    RetryPolicy = new ClientRetryPolicy(0),
                    Transport = new HttpClientPipelineTransport(connection.HttpClient, true, loggerFactory)
                }
            ).AsIChatClient(Configuration.ModelId ?? string.Empty),
            this
        ).AsChatCompletionService();
    }

    /// <inheritdoc />
    public override void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractOpenAI(evidence);
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
                foreach (var content in update.Contents)
                {
                    if (content is ErrorContent errorContent)
                        throw ChatExceptionNormalizer.FromErrorCode(
                            new Exception(errorContent.Message),
                            errorContent.ErrorCode ?? errorContent.Message,
                            owner);
                }

                yield return update;
            }
        }

        private CreateResponseOptions RawRepresentationFactory(IChatClient _)
        {
            var options = owner._options;
            var reasoningEffortLevel = options.ResolveReasoningEffort(owner.Configuration)switch
            {
                { Length: > 0 } reasoningEffort => new ResponseReasoningEffortLevel(reasoningEffort),
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
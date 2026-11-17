using System.ClientModel;
using System.ClientModel.Primitives;
using System.Globalization;
using Everywhere.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Responses;

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for OpenAI Responses API endpoints.
/// </summary>
public sealed class OpenAIResponsesModelSession : ModelSession
{
    public const string DialectName = "openai-responses";

    /// <inheritdoc />
    public override IChatClient ChatClient { get; }

    /// <inheritdoc />
    public override string Dialect => DialectName;

    /// <inheritdoc />
    public override bool SupportsToolResultBlocks => true;

    private readonly OpenAIResponsesOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIResponsesModelSession"/> class.
    /// </summary>
    public OpenAIResponsesModelSession(
        AssistantConfiguration configuration,
        OpenAIResponsesOptions options,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(configuration, connection)
    {
        _options = options;

        ChatClient = new OptimizedChatClient(
            new ResponsesClient(
                new ApiKeyCredential(ApiKey ?? "NO_API_KEY"),
                new ResponsesClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    RetryPolicy = new ClientRetryPolicy(0),
                    Transport = new HttpClientPipelineTransport(connection.HttpClient, true, loggerFactory)
                }
            ).AsIChatClient(Configuration.ModelId ?? string.Empty),
            this);
    }

    /// <inheritdoc />
    public override bool HasToolCallUpdate(ChatResponseUpdate update) =>
        base.HasToolCallUpdate(update) ||
        update.RawRepresentation is StreamingResponseOutputItemAddedUpdate { Item: FunctionCallResponseItem }
            or StreamingResponseFunctionCallArgumentsDeltaUpdate;

    /// <inheritdoc />
    public override void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractOpenAI(evidence);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        ChatClient.Dispose();
    }

    private sealed class OptimizedChatClient(IChatClient originalClient, OpenAIResponsesModelSession owner) : DelegatingChatClient(originalClient)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            foreach (var message in response.Messages)
            foreach (var content in message.Contents)
            {
                if (content is ErrorContent errorContent)
                {
                    throw ChatExceptionNormalizer.FromErrorCode(
                        new Exception(errorContent.Message),
                        errorContent.ErrorCode ?? errorContent.Message,
                        owner);
                }
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                foreach (var content in update.Contents)
                {
                    if (content is ErrorContent errorContent)
                    {
                        throw ChatExceptionNormalizer.FromErrorCode(
                            new Exception(errorContent.Message),
                            errorContent.ErrorCode ?? errorContent.Message,
                            owner);
                    }
                }

                yield return update;
            }
        }

        private CreateResponseOptions RawRepresentationFactory(IChatClient _)
        {
            var options = owner._options;
            var reasoningEffortLevel = options.ResolveReasoningEffort(owner.Configuration) switch
            {
                { Length: > 0 } reasoningEffort => new ResponseReasoningEffortLevel(reasoningEffort),
                _ => (ResponseReasoningEffortLevel?)null
            };
            var reasoningSummaryVerbosity = options.ReasoningSummary switch
            {
                { Length: > 0 } => new ResponseReasoningSummaryVerbosity(options.ReasoningSummary),
                _ => (ResponseReasoningSummaryVerbosity?)null
            };
            var createOptions = new CreateResponseOptions
            {
                Temperature = float.TryParse(options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : null,
                TopP = float.TryParse(options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : null,
                ReasoningOptions = new ResponseReasoningOptions
                {
                    ReasoningEffortLevel = reasoningEffortLevel,
                    ReasoningSummaryVerbosity = reasoningSummaryVerbosity
                }
            };
            createOptions.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
            return createOptions;
        }
    }
}

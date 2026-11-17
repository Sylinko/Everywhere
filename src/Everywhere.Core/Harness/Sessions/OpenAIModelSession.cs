using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Everywhere.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Chat;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for OpenAI-compatible Chat Completions endpoints.
/// </summary>
public sealed class OpenAIModelSession : ModelSession
{
    public const string DialectName = "openai";

    /// <inheritdoc />
    public override IChatClient ChatClient { get; }

    /// <inheritdoc />
    public override string Dialect => DialectName;

    private readonly OpenAIOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="OpenAIModelSession"/> class.
    /// </summary>
    public OpenAIModelSession(
        AssistantConfiguration configuration,
        OpenAIOptions options,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(configuration, connection)
    {
        _options = options;

        // Some models do not need an API key (e.g. LM Studio, Official mode).
        AuthenticationPolicy authenticationPolicy = ApiKey.IsNullOrWhiteSpace() ?
            new NoneAuthenticationPolicy() :
            ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(new ApiKeyCredential(ApiKey));

        ChatClient = new OptimizedOpenAIApiClient(
            new ChatClient(
                Configuration.ModelId ?? string.Empty,
                authenticationPolicy,
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    RetryPolicy = new ClientRetryPolicy(0),
                    Transport = new HttpClientPipelineTransport(connection.HttpClient, true, loggerFactory)
                }
            ).AsIChatClient(),
            this);
    }

    /// <inheritdoc />
    public override bool HasToolCallUpdate(ChatResponseUpdate update) =>
        base.HasToolCallUpdate(update) ||
        update.RawRepresentation is StreamingChatCompletionUpdate { ToolCallUpdates.Count: > 0 };

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

    private sealed class OptimizedOpenAIApiClient(IChatClient client, OpenAIModelSession owner) : DelegatingChatClient(client)
    {
        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var prepared = PrepareRequest(messages, ref options);
            return base.GetResponseAsync(prepared, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var prepared = PrepareRequest(messages, ref options);
            await foreach (var update in base.GetStreamingResponseAsync(prepared, options, cancellationToken))
            {
                yield return update;
            }
        }

        [UnsafeAccessor(UnsafeAccessorKind.StaticMethod, Name = "ToOpenAIChatMessages")]
        private extern static IEnumerable<OpenAI.Chat.ChatMessage> ToOpenAIChatMessages(
            [UnsafeAccessorType("Microsoft.Extensions.AI.OpenAIChatClient, Microsoft.Extensions.AI.OpenAI")]
            object? klass,
            IEnumerable<ChatMessage> inputs,
            ChatOptions? chatOptions);

        private List<ChatMessage> PrepareRequest(IEnumerable<ChatMessage> messages, ref ChatOptions? options)
        {
            var messagesList = messages.AsValueEnumerable().Select(static m => m.Clone()).ToList();
            options = OpenAICompatibleToolSchemaTransformer.Transform(options);
            options = options?.Clone() ?? new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.RawRepresentationFactory ??= RawRepresentationFactory;

            if (!owner._options.IncludeReasoningContent ||
                !messagesList.AsValueEnumerable().Any(static m => m.Role == ChatRole.Assistant))
            {
                return messagesList;
            }

            var openAIMessages = ToOpenAIChatMessages(null, messagesList, options);
            using (var openAIMessagesEnumerator = openAIMessages.GetEnumerator())
            {
                openAIMessagesEnumerator.MoveNext();

                foreach (var originalMessage in messagesList.AsValueEnumerable())
                {
                    if (originalMessage.Role == ChatRole.Tool)
                    {
                        var rawRepresentations = new List<ToolChatMessage>();
                        while (openAIMessagesEnumerator.Current is ToolChatMessage toolChatMessage)
                        {
                            rawRepresentations.Add(toolChatMessage);
                            if (!openAIMessagesEnumerator.MoveNext()) break;
                        }

                        originalMessage.RawRepresentation = rawRepresentations;
                    }
                    else
                    {
                        originalMessage.RawRepresentation = openAIMessagesEnumerator.Current;
                        openAIMessagesEnumerator.MoveNext();
                    }
                }
            }

            foreach (var assistantMessage in messagesList.AsValueEnumerable().Where(static m => m.Role == ChatRole.Assistant))
            {
                Debug.Assert(assistantMessage.RawRepresentation is OpenAI.Chat.ChatMessage);
                if (assistantMessage.RawRepresentation is not OpenAI.Chat.ChatMessage chatMessage) continue;

                var patch = new JsonPatch();
                var reasoningContent = string.Concat(
                    assistantMessage.Contents.OfType<TextReasoningContent>().Select(static c => c.Text));
                patch.Set("$.reasoning_content"u8, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reasoningContent)));
                chatMessage.Patch = patch;
            }

            return messagesList;
        }

        private ChatCompletionOptions RawRepresentationFactory(IChatClient _)
        {
            var options = owner._options;

            var thinkingPatch = new JsonPatch();
            if (options.ThinkingType is { Length: > 0 })
            {
                thinkingPatch.Set(
                    "$.thinking"u8,
                    Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = options.ThinkingType })));
            }

            return new ChatCompletionOptions
            {
                Temperature = float.TryParse(options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : null,
                TopP = float.TryParse(options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : null,
                PresencePenalty = float.TryParse(options.PresencePenalty, NumberStyles.Float, CultureInfo.InvariantCulture, out var presencePenalty) ? presencePenalty : null,
                FrequencyPenalty = float.TryParse(options.FrequencyPenalty, NumberStyles.Float, CultureInfo.InvariantCulture, out var frequencyPenalty) ? frequencyPenalty : null,
                ReasoningEffortLevel = options.ResolveReasoningEffort(owner.Configuration) switch
                {
                    { Length: > 0 } reasoningEffort => new ChatReasoningEffortLevel?(reasoningEffort),
                    _ => null
                },
                Patch = thinkingPatch,
            };
        }
    }

    private sealed class NoneAuthenticationPolicy : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNext(message, pipeline, currentIndex);

        public override ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex) =>
            ProcessNextAsync(message, pipeline, currentIndex);
    }
}
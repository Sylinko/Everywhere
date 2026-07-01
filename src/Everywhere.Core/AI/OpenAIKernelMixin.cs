using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;
using OpenAI;
using OpenAI.Chat;
using ZLinq;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

#pragma warning disable SCME0001 // Type is for evaluation purposes only and is subject to change or removal in future updates.

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for OpenAI models via Chat Completions.
/// </summary>
public class OpenAIKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly OpenAIOptions _options;

    public OpenAIKernelMixin(
        Assistant assistant,
        ModelConnection connection,
        ILoggerFactory loggerFactory
    ) : base(assistant, connection)
    {
        _options = assistant.OpenAIOptions;

        // Some models don't need API key (e.g. LM Studio, Official mode)
        AuthenticationPolicy authenticationPolicy = ApiKey.IsNullOrWhiteSpace() ?
            new NoneAuthenticationPolicy() :
            ApiKeyAuthenticationPolicy.CreateBearerAuthorizationPolicy(new ApiKeyCredential(ApiKey));

        ChatCompletionService = new OptimizedOpenAIApiClient(
            new ChatClient(
                ModelId,
                authenticationPolicy,
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(Endpoint, UriKind.Absolute),
                    Transport = new HttpClientPipelineTransport(connection.HttpClient, true, loggerFactory)
                }
            ).AsIChatClient(),
            this
        ).AsChatCompletionService();
    }

    /// <summary>
    /// optimized wrapper around MEAI's IChatClient to extract reasoning content from internal properties.
    /// </summary>
    private sealed class OptimizedOpenAIApiClient(IChatClient client, OpenAIKernelMixin owner) : DelegatingChatClient(client)
    {
        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // snapshot messages for modification
            var messagesList = messages.AsValueEnumerable().ToList();

            // early convert to OpenAI chat messages and store raw representation
            // so that we can modify them in BeforeStreamingRequestAsync if needed
            var openAIMessages = ToOpenAIChatMessages(null, messagesList, options);
            using (var openAIMessagesEnumerator = openAIMessages.GetEnumerator())
            {
                openAIMessagesEnumerator.MoveNext();

                foreach (var originalMessage in messagesList.AsValueEnumerable())
                {
                    // Each Tool Message may correspond to multiple OpenAI messages, so we need to handle them differently.
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

            BeforeStreamingRequestHook(messagesList, ref options);

            // cache the value to avoid property changes during enumeration
            await foreach (var update in base.GetStreamingResponseAsync(messagesList, options, cancellationToken))
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

        /// <summary>
        /// Hook called before sending a streaming chat request.
        /// </summary>
        private void BeforeStreamingRequestHook(List<ChatMessage> messages, ref ChatOptions? options)
        {
            if (!owner._options.IncludeReasoningContent) return;

            options ??= new ChatOptions();
            options.AdditionalProperties ??= new AdditionalPropertiesDictionary();
            options.RawRepresentationFactory ??= RawRepresentationFactory;

            foreach (var assistantMessage in messages.AsValueEnumerable().Where(m => m.Role == ChatRole.Assistant))
            {
                Debug.Assert(assistantMessage.RawRepresentation is OpenAI.Chat.ChatMessage);
                if (assistantMessage.RawRepresentation is not OpenAI.Chat.ChatMessage chatMessage) continue;

                var patch = new JsonPatch();
                // patch.Set("$.reasoning_content"u8, string.Empty) will throw an exception: Empty encoded value
                // It seems a bug in the JsonPatch implementation, so we need to encode the empty string to bytes manually.
                var reasoningContent = assistantMessage.Contents.AsValueEnumerable().OfType<TextReasoningContent>().FirstOrDefault()?.Text;
                patch.Set("$.reasoning_content"u8, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reasoningContent ?? string.Empty)));
                chatMessage.Patch = patch;
            }
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
                Temperature = float.TryParse(options.Temperature, out var temperature) ? temperature : null,
                TopP = float.TryParse(options.TopP, out var topP) ? topP : null,
                PresencePenalty = float.TryParse(options.PresencePenalty, out var presencePenalty) ? presencePenalty : null,
                FrequencyPenalty = float.TryParse(options.FrequencyPenalty, out var frequencyPenalty) ? frequencyPenalty : null,
                ReasoningEffortLevel = options.ReasoningEffort switch
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
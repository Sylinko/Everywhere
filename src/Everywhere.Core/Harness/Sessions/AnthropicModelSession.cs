using System.Globalization;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Everywhere.AI;
using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// <see cref="ModelSession"/> implementation for Anthropic Messages API models.
/// </summary>
public sealed partial class AnthropicModelSession : ModelSession
{
    public const string DialectName = "anthropic";

    /// <inheritdoc />
    public override IChatClient ChatClient => _client;

    /// <inheritdoc />
    public override string Dialect => DialectName;

    /// <inheritdoc />
    public override bool SupportsToolResultBlocks => true;

    private readonly AnthropicOptions _options;
    private readonly OptimizedChatClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicModelSession"/> class.
    /// </summary>
    public AnthropicModelSession(
        AssistantConfiguration configuration,
        AnthropicOptions options,
        ModelConnection connection
    ) : base(configuration, connection)
    {
        _options = options;

        _client = new OptimizedChatClient(
            new AnthropicClient(
                new ClientOptions
                {
                    ApiKey = ApiKey,
                    HttpClient = connection.HttpClient,
                    BaseUrl = Endpoint,
                    Timeout = connection.HttpClient.Timeout,
                    MaxRetries = 0
                }).AsIChatClient(),
            this);
    }

    /// <inheritdoc />
    public override bool HasToolCallUpdate(ChatResponseUpdate update) =>
        base.HasToolCallUpdate(update) ||
        update.RawRepresentation is RawMessageStreamEvent
        {
            Value: RawContentBlockStartEvent { ContentBlock.Value: ToolUseBlock }
                or RawContentBlockDeltaEvent { Delta.Value: InputJsonDelta }
        };

    /// <inheritdoc />
    public override void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractAnthropic(evidence);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _client.Dispose();
    }

    private sealed partial class OptimizedChatClient : DelegatingChatClient
    {
        private readonly AnthropicModelSession _owner;
        private readonly bool _isClaude;
        private readonly bool _isAdaptiveReasoningSupported;

        public OptimizedChatClient(IChatClient originalClient, AnthropicModelSession owner) : base(originalClient)
        {
            _owner = owner;
            var modelId = owner.Configuration.ModelId ?? string.Empty;
            _isClaude = modelId.Contains("claude", StringComparison.OrdinalIgnoreCase);
            _isAdaptiveReasoningSupported = IsAdaptiveReasoningSupported(_isClaude, modelId);
        }

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            var response = await base.GetResponseAsync(PreprocessMessages(messages), options, cancellationToken);
            foreach (var message in response.Messages)
            foreach (var content in message.Contents)
            {
                if (content is TextReasoningContent { RawRepresentation: RedactedThinkingBlock or ContentBlock { Value: RedactedThinkingBlock } } reasoning)
                {
                    (reasoning.AdditionalProperties ??= [])[ReasoningMetadata.IsRedactedKey] = true;
                }
            }

            return response;
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            await foreach (var update in base.GetStreamingResponseAsync(PreprocessMessages(messages), options, cancellationToken))
            {
                var isRedactedUpdate = update.RawRepresentation is RawMessageStreamEvent
                {
                    Value: RawContentBlockStartEvent { ContentBlock.Value: RedactedThinkingBlock }
                };
                foreach (var content in update.Contents)
                {
                    if (content is TextReasoningContent reasoning &&
                        (isRedactedUpdate || reasoning.RawRepresentation is RedactedThinkingBlock or ContentBlock { Value: RedactedThinkingBlock }))
                    {
                        (reasoning.AdditionalProperties ??= [])[ReasoningMetadata.IsRedactedKey] = true;
                    }
                }

                if (update.RawRepresentation is RawMessageStreamEvent { Value: RawContentBlockStopEvent })
                {
                    (update.AdditionalProperties ??= [])[ReasoningMetadata.BlockBoundaryKey] = true;
                }

                yield return update;
            }
        }

        private void BuildOptions(ref ChatOptions? options)
        {
            options = options?.Clone() ?? new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;
            options.Temperature = float.TryParse(_owner._options.Temperature, NumberStyles.Float, CultureInfo.InvariantCulture, out var temperature) ? temperature : null;
            options.TopP = float.TryParse(_owner._options.TopP, NumberStyles.Float, CultureInfo.InvariantCulture, out var topP) ? topP : null;
            options.TopK = int.TryParse(_owner._options.TopK, NumberStyles.Integer, CultureInfo.InvariantCulture, out var topK) ? topK : null;
        }

        private MessageCreateParams RawRepresentationFactory(IChatClient _)
        {
            var options = _owner._options;

            ThinkingConfigParam thinkingConfigParam;
            if (options.ThinkingConfig == AnthropicRequestThinkingConfig.Disabled)
            {
                thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigDisabled());
            }
            else if (options.ThinkingConfig == AnthropicRequestThinkingConfig.Adaptive || _isAdaptiveReasoningSupported)
            {
                thinkingConfigParam = new ThinkingConfigParam(new ThinkingConfigAdaptive());
            }
            else
            {
                thinkingConfigParam = new ThinkingConfigParam(
                    new ThinkingConfigEnabled
                    {
                        BudgetTokens = Math.Max(options.BudgetTokens, 2048)
                    });
            }

            OutputConfig? outputConfig = null;
            var reasoningEffort = options.ResolveReasoningEffort(_owner.Configuration);
            if (!reasoningEffort.IsNullOrWhiteSpace())
            {
                outputConfig = new OutputConfig
                {
                    Effort = reasoningEffort
                };
            }

            return new MessageCreateParams
            {
                MaxTokens = _owner.Configuration.OutputLimit switch
                {
                    > 0 => _owner.Configuration.OutputLimit,
                    _ => 4096,
                },
                Messages = [], // Leave empty and underlying implementation will populate it.
                Model = _owner.Configuration.ModelId ?? string.Empty,
                Thinking = thinkingConfigParam,
                OutputConfig = outputConfig,
                CacheControl = options.CacheControl is AnthropicRequestCacheControl.Ephemeral ? new CacheControlEphemeral() : null
            };
        }

        private List<ChatMessage> PreprocessMessages(IEnumerable<ChatMessage> originalMessages)
        {
            var processed = new List<ChatMessage>();
            foreach (var message in originalMessages)
            {
                var clone = message.Clone();
                for (var i = clone.Contents.Count - 1; i >= 0; i--)
                {
                    if (clone.Contents[i] is not TextReasoningContent reasoning) continue;

                    if (_isClaude && string.IsNullOrEmpty(reasoning.ProtectedData))
                    {
                        // Unsigned or foreign reasoning blocks are rejected by Claude extended thinking.
                        clone.Contents.RemoveAt(i);
                        continue;
                    }

                    var isRedacted = reasoning.AdditionalProperties?.TryGetValue(ReasoningMetadata.IsRedactedKey, out var rawRedacted) is true && rawRedacted is true;
                    if (isRedacted && !string.IsNullOrEmpty(reasoning.ProtectedData))
                    {
                        clone.Contents[i] = new TextReasoningContent(null)
                        {
                            ProtectedData = reasoning.ProtectedData,
                            RawRepresentation = new ContentBlockParam(
                                new RedactedThinkingBlockParam
                                {
                                    Data = reasoning.ProtectedData
                                })
                        };
                    }
                    else if (string.IsNullOrEmpty(reasoning.Text) && !string.IsNullOrEmpty(reasoning.ProtectedData))
                    {
                        // Anthropic's MEAI adapter maps empty-text ProtectedData to RedactedThinkingBlockParam unless
                        // RawRepresentation supplies an explicit ThinkingBlockParam (e.g. Claude 4.6 signature-only thinking).
                        clone.Contents[i] = new TextReasoningContent(string.Empty)
                        {
                            ProtectedData = reasoning.ProtectedData,
                            RawRepresentation = new ContentBlockParam(
                                new ThinkingBlockParam
                                {
                                    Thinking = string.Empty,
                                    Signature = reasoning.ProtectedData
                                })
                        };
                    }
                }

                processed.Add(clone);
            }

            return processed;
        }

        private static bool IsAdaptiveReasoningSupported(bool isClaude, string modelId)
        {
            if (!isClaude) return false;

            var match = ClaudeVersionRegex().Match(modelId);
            if (!match.Success) return true;

            var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
            var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture) : 0;
            return major > 4 || major == 4 && minor >= 6;
        }

        [GeneratedRegex(
            @"claude-(?:(?:fable|opus|sonnet|haiku)-)?(?<major>\d+)(?:[.-](?<minor>\d)(?=$|[@\-:.]))?(?:-(?:fable|opus|sonnet|haiku))?",
            RegexOptions.IgnoreCase)]
        private static partial Regex ClaudeVersionRegex();
    }
}
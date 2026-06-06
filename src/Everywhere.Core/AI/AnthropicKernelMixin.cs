using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Core;
using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

/// <summary>
/// An implementation of <see cref="KernelMixin"/> for Anthropic models.
/// </summary>
public sealed partial class AnthropicKernelMixin : KernelMixin
{
    public override IChatCompletionService ChatCompletionService { get; }

    private readonly AnthropicOptions _options;
    private readonly OptimizedChatClient _client;

    /// <summary>
    /// Initializes a new instance of the <see cref="AnthropicKernelMixin"/> class.
    /// </summary>
    public AnthropicKernelMixin(Assistant assistant, ModelConnection connection) : base(assistant, connection)
    {
        _options = assistant.AnthropicOptions;

        _client = new OptimizedChatClient(
            new AnthropicClient(
                new ClientOptions
                {
                    ApiKey = ApiKey,
                    HttpClient = connection.HttpClient,
                    BaseUrl = Endpoint,
                    Timeout = TimeSpan.FromSeconds(Math.Clamp(assistant.RequestTimeoutSeconds, 1, 24 * 60 * 60))
                }).AsIChatClient(),
            this);
        ChatCompletionService = _client.AsChatCompletionService();
    }

    public override bool IsPersistentSpanMetadataKey(string key) => key == "ProtectedData";

    public override void Dispose()
    {
        _client.Dispose();
    }

    private sealed partial class OptimizedChatClient : DelegatingChatClient
    {
        private readonly AnthropicKernelMixin _owner;
        private readonly bool _isClaude;
        private readonly bool _isAdaptiveReasoningSupported;

        public OptimizedChatClient(IChatClient originalClient, AnthropicKernelMixin owner) : base(originalClient)
        {
            _owner = owner;
            _isClaude = owner.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase);
            _isAdaptiveReasoningSupported = IsAdaptiveReasoningSupported(_isClaude, owner.ModelId);
        }

        private void BuildOptions(ref ChatOptions? options)
        {
            options ??= new ChatOptions();
            options.RawRepresentationFactory = RawRepresentationFactory;

            if (_owner.Temperature is { } temperature) options.Temperature = (float)temperature;
            if (_owner.TopP is { } topP) options.TopP = (float)topP;
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
            if (options.ThinkingEffort is { Length: > 0 } effort)
            {
                outputConfig = new OutputConfig
                {
                    Effort = effort
                };
            }

            return new MessageCreateParams
            {
                MaxTokens = _owner.OutputLimit switch
                {
                    > 0 => _owner.OutputLimit,
                    _ => 4096,
                },
                Messages = [], // Leave empty and underlying implementation will handle it
                Model = _owner.ModelId,
                Thinking = thinkingConfigParam,
                OutputConfig = outputConfig,
                CacheControl = options.CacheControl is AnthropicRequestCacheControl.Ephemeral ? new CacheControlEphemeral() : null
            };
        }

        private IEnumerable<ChatMessage> PreprocessMessages(IEnumerable<ChatMessage> originalMessages)
        {
            if (_isClaude)
            {
                return originalMessages.Invoke(static chatMessage =>
                {
                    for (var i = chatMessage.Contents.Count - 1; i >= 0; i--)
                    {
                        // Remove those TextReasoningContent with empty ProtectedData as they are likely to cause issues
                        // for claude models that don't support reasoning effort and expect the content to be text-only.
                        if (chatMessage.Contents[i] is TextReasoningContent { ProtectedData: not { Length: > 0 } })
                        {
                            chatMessage.Contents.RemoveAt(i);
                        }
                    }
                });
            }

            return originalMessages;
        }

        public override Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetResponseAsync(PreprocessMessages(messages), options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            BuildOptions(ref options);
            return base.GetStreamingResponseAsync(PreprocessMessages(messages), options, cancellationToken);
        }

        private static bool IsAdaptiveReasoningSupported(bool isClaude, string modelId)
        {
            if (!isClaude) return false;

            var match = ClaudeVersionRegex().Match(modelId);
            if (!match.Success) return true;

            var major = int.Parse(match.Groups["major"].Value);
            var minor = match.Groups["minor"].Success ? int.Parse(match.Groups["minor"].Value) : 0;
            return major > 4 || major == 4 && minor >= 6;
        }

        [GeneratedRegex(@"claude-(?:(?:opus|sonnet|haiku)-)?(?<major>\d+)(?:[.-](?<minor>\d)(?=$|[@\-:.]))?(?:-(?:opus|sonnet|haiku))?", RegexOptions.IgnoreCase)]
        private static partial Regex ClaudeVersionRegex();
    }
}

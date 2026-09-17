using Everywhere.AI.Prompts;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract class KernelMixin(AssistantConfiguration configuration, ModelConnection connection) : IDisposable
{
    /// <summary>
    /// Gets the model configuration snapshot used for this mixin's complete lifetime.
    /// </summary>
    public AssistantConfiguration Configuration { get; } = configuration;

    /// <summary>
    /// Convenience accessor for the resolved endpoint (already normalized, never null).
    /// </summary>
    protected string Endpoint { get; } = connection.Endpoint;

    /// <summary>
    /// Convenience accessor for the resolved API key (null means no key needed / handled by HttpClient).
    /// </summary>
    protected string? ApiKey { get; } = connection.ApiKey;

    public abstract IChatCompletionService ChatCompletionService { get; }

    private readonly HttpClient _httpClient = connection.HttpClient;

    public virtual bool IsPersistentMessageMetadataKey(string key) => false;

    public virtual bool IsPersistentSpanMetadataKey(string key) => false;

    /// <summary>
    /// Default implementation includes temperature and top_p from the custom assistant.
    /// </summary>
    /// <param name="functionChoiceBehavior"></param>
    /// <returns></returns>
    public virtual PromptExecutionSettings GetPromptExecutionSettings(FunctionChoiceBehavior? functionChoiceBehavior = null) => new()
    {
        FunctionChoiceBehavior = functionChoiceBehavior
    };

    public async Task CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var innerCancellationTokenSource = new CancellationTokenSource();
        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            innerCancellationTokenSource.Token);

        await foreach (var _ in ChatCompletionService.GetStreamingChatMessageContentsAsync(
            [
                new ChatMessageContent(AuthorRole.System, "You're a helpful assistant."),
                new ChatMessageContent(AuthorRole.User, DefaultPrompts.TestPrompt)
            ],
            GetPromptExecutionSettings(),
            cancellationToken: linkedCancellationTokenSource.Token))
        {
            // if we can get any response without exception, we consider the connectivity check passed, then we can cancel the request to avoid unnecessary cost.
            await innerCancellationTokenSource.CancelAsync();
            return;
        }
    }

    /// <summary>
    /// Transform exceptions thrown by the chat completion service.
    /// This allows us to convert exceptions from the underlying SDK into HandledChatException with specific types,
    /// so that the UI can show more user-friendly error messages and take different actions based on the exception type.
    /// </summary>
    /// <param name="exception"></param>
    /// <returns></returns>
    public Exception TransformChatException(Exception exception) => connection.ChatExceptionTransformer?.Invoke(exception) ?? exception;

    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        _httpClient.Dispose();
    }
}
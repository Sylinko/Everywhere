using Everywhere.Common;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract class KernelMixin(Assistant assistant, ModelConnection connection) : IModelDefinition, IDisposable
{
    /// <summary>
    /// Convenience accessor for the resolved endpoint (already normalized, never null).
    /// </summary>
    protected string Endpoint { get; } = connection.Endpoint;

    /// <summary>
    /// Convenience accessor for the resolved API key (null means no key needed / handled by HttpClient).
    /// </summary>
    protected string? ApiKey { get; } = connection.ApiKey;

    public string ModelId { get; } = assistant.ModelId ??
        throw new HandledChatException(
            new InvalidOperationException("Model ID cannot be empty."),
            HandledChatExceptionType.InvalidConfiguration);

    public bool SupportsToolCall { get; } = assistant.SupportsToolCall;

    public Modalities InputModalities { get; } = assistant.InputModalities;

    public Modalities OutputModalities { get; } = assistant.OutputModalities;

    public int ContextLimit { get; } = assistant.ContextLimit;

    public int OutputLimit { get; } = assistant.OutputLimit;

    public ModelSpecializations Specializations { get; } = assistant.Specializations;

    public DateOnly? DeprecationDate { get; } = assistant.DeprecationDate;

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
                               new ChatMessageContent(AuthorRole.User, Prompts.TestPrompt)
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
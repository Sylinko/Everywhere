using Everywhere.AI.Prompts;
using Everywhere.Common;
using Microsoft.Extensions.AI;

namespace Everywhere.AI;

/// <summary>
/// Owns one immutable <see cref="AssistantConfiguration"/> snapshot, its transport <see cref="ModelConnection"/>,
/// and the native MEAI <see cref="IChatClient"/> used to execute model requests.
/// </summary>
public abstract partial class ModelSession(AssistantConfiguration configuration, ModelConnection connection) : IDisposable
{
    /// <summary>
    /// Gets the model configuration snapshot used for this session's complete lifetime.
    /// </summary>
    public AssistantConfiguration Configuration => configuration;

    /// <summary>
    /// Gets the native MEAI chat client bound to this session.
    /// </summary>
    public abstract IChatClient ChatClient { get; }

    /// <summary>
    /// Gets the wire dialect identifier used to scope provider-specific continuation metadata.
    /// </summary>
    public abstract string Dialect { get; }

    /// <summary>
    /// Gets whether tool results can carry ordered text and image blocks directly inside the tool message.
    /// Other providers receive tool text followed by a user attachment projection.
    /// </summary>
    public virtual bool SupportsToolResultBlocks => false;

    /// <summary>
    /// Gets the immutable retry budget captured with this connection.
    /// </summary>
    public int RequestMaxRetries => connection.RequestMaxRetries;

    /// <summary>
    /// Convenience accessor for the resolved endpoint (already normalized, never null).
    /// </summary>
    protected string Endpoint => connection.Endpoint;

    /// <summary>
    /// Convenience accessor for the resolved API key (<see langword="null"/> means no key needed or handled by <see cref="HttpClient"/>).
    /// </summary>
    protected string? ApiKey => connection.ApiKey;

    /// <summary>
    /// Creates provider-configured <see cref="ChatOptions"/> for one request.
    /// </summary>
    public virtual ChatOptions CreateChatOptions(IList<AITool>? tools = null, ChatToolMode? toolMode = null) => new()
    {
        Tools = tools,
        ToolMode = toolMode
    };

    /// <summary>
    /// Gets whether the given streaming update indicates active client tool-call generation.
    /// </summary>
    public virtual bool HasToolCallUpdate(ChatResponseUpdate update) =>
        update.Contents.AsValueEnumerable().OfType<FunctionCallContent>().Any(static call => !call.InformationalOnly);

    /// <summary>
    /// Probes connectivity with a minimal non-retried streaming request.
    /// </summary>
    public async Task CheckConnectivityAsync(CancellationToken cancellationToken = default)
    {
        var streamRequest = StreamRequestAsync(
            [
                new ChatMessage(ChatRole.System, "You're a helpful assistant."),
                new ChatMessage(ChatRole.User, DefaultPrompts.TestPrompt)
            ],
            CreateChatOptions(),
            requestOptions: new ChatRequestOptions(0),
            cancellationToken: cancellationToken);
        await foreach (var update in streamRequest)
        {
            // Leaving await foreach disposes the SDK stream, without a second request.
            if (update is ChatRequestUpdate.Content) return;
        }
    }

    /// <summary>
    /// Extracts SDK-specific error evidence without selecting classification or retry policy.
    /// </summary>
    public virtual void ExtractExceptionEvidence(ChatExceptionEvidence evidence)
    {
        ChatExceptionEvidenceExtractor.ExtractSdkEvidence(evidence);
    }

    /// <summary>
    /// Recognizes a concrete SDK failure without re-encoding its cause as a category identifier.
    /// </summary>
    public virtual HandledChatException? NormalizeKnownException(ChatExceptionEvidence evidence) =>
        ChatExceptionNormalizer.NormalizeKnownSdkException(evidence);

    /// <summary>
    /// Enriches connection-specific evidence after SDK extraction.
    /// </summary>
    public void EnrichConnectionExceptionEvidence(ChatExceptionEvidence evidence)
    {
        connection.ExceptionEvidenceEnricher?.Invoke(evidence);
    }

    /// <inheritdoc />
    public virtual void Dispose()
    {
        GC.SuppressFinalize(this);
        connection.Dispose();
    }
}
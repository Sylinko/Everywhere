using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Harness;

/// <summary>
/// Owns one generation's model resources, service scope, approval state, and ambient tool invocation.
/// </summary>
/// <remarks>
/// <see cref="ChatService"/> publishes this context after initialization and clears it in the same generation's
/// <see langword="finally"/> block before disposing the session and services. A child shares only <see cref="ApprovalState"/>;
/// its services, session, and ambient invocation remain independent. Manual compaction sets <see cref="IsConversationTurn"/> to <see langword="false"/>.
/// </remarks>
public sealed class GenerationContext(
    IServiceProvider services,
    ModelSession session,
    IChatPluginScope? pluginScope,
    IPromptRenderer promptRenderer,
    string systemPrompt,
    Modalities inputModalities,
    int contextCompressionThreshold,
    int maxContextRounds,
    GenerationApprovalState approvalState,
    bool isConversationTurn,
    ChatMessageNode appendTailNode,
    Assistant? assistant = null
) : IAsyncDisposable
{
    /// <summary>
    /// Gets the last node on this execution's path, independently of displayed branch choices.
    /// A pre-output compression may advance this node without replacing the writable assistant.
    /// </summary>
    public ChatMessageNode AppendTailNode { get; private set; } = appendTailNode;

    /// <summary>
    /// Gets the assistant currently accepting output, if this is a conversational generation.
    /// </summary>
    public ChatMessageNode? CurrentAssistantNode { get; private set; } =
        isConversationTurn && appendTailNode.Message is AssistantChatMessage ? appendTailNode : null;

    /// <summary>
    /// Gets the service provider built for this generation.
    /// </summary>
    public IServiceProvider Services { get; } = services;

    /// <summary>
    /// Gets the executing assistant's immutable configuration and provider session.
    /// </summary>
    public ModelSession Session { get; } = session;

    /// <summary>
    /// Gets the scoped tool snapshot resolved for this generation, if tool calling is active.
    /// </summary>
    public IChatPluginScope? PluginScope { get; } = pluginScope;

    /// <summary>
    /// Gets the renderer scoped to the generation's prompt placeholders.
    /// </summary>
    public IPromptRenderer PromptRenderer { get; } = promptRenderer;

    /// <summary>
    /// Gets the rendered execution constraints supplied to the executing assistant.
    /// </summary>
    public string SystemPrompt { get; } = systemPrompt;

    /// <summary>
    /// Gets the accepted input modalities from the executing configuration snapshot.
    /// </summary>
    public Modalities InputModalities { get; } = inputModalities;

    /// <summary>
    /// Gets the configured automatic compression threshold percentage.
    /// </summary>
    public int ContextCompressionThreshold { get; } = contextCompressionThreshold;

    /// <summary>
    /// Gets the configured history round limit.
    /// </summary>
    public int MaxContextRounds { get; } = maxContextRounds;

    /// <summary>
    /// Gets the mode state shared with ordinary children of this turn.
    /// </summary>
    public GenerationApprovalState ApprovalState { get; } = approvalState;

    /// <summary>
    /// Gets the mode that the next invocation will capture.
    /// </summary>
    public ToolApprovalMode ApprovalMode => ApprovalState.Mode;

    /// <summary>
    /// Gets whether this context belongs to a conversational generation rather than manual compaction.
    /// </summary>
    public bool IsConversationTurn { get; } = isConversationTurn;

    /// <summary>
    /// Isolates ambient invocation services within this generation's asynchronous flows.
    /// </summary>
    public AsyncLocal<FunctionCallContext?> FunctionCallContext { get; } = new();

    /// <summary>
    /// Gets the resolved assistant configuration that started this generation, if known.
    /// </summary>
    public Assistant? Assistant { get; } = assistant ?? services.GetService<Assistant>();

    /// <summary>
    /// Gets whether the foreground generation owning this context has released its lease.
    /// </summary>
    public bool IsDisposed => Volatile.Read(ref _isDisposed) != 0;

    private int _isDisposed;
    private int _activeHolders = 1;

    /// <summary>
    /// Appends an execution-owned node without consulting the displayed branch.
    /// </summary>
    public ChatMessageNode Append(ChatMessage message)
    {
        AppendTailNode = AppendTailNode.Data.Append(AppendTailNode, message);
        if (message is AssistantChatMessage)
        {
            CurrentAssistantNode = AppendTailNode;
        }

        return AppendTailNode;
    }

    /// <summary>
    /// Advances <see cref="AppendTailNode"/> after helper pipeline stages (such as visual attachment analysis) append child nodes.
    /// </summary>
    public void AdvanceAppendTailNode(ChatMessageNode node)
    {
        AppendTailNode = node;
    }

    /// <summary>
    /// Captures the parent path used by the next provider request.
    /// </summary>
    public IReadOnlyList<ChatMessageNode> GetPath()
    {
        return AppendTailNode.Data.GetPathTo(AppendTailNode);
    }

    /// <summary>
    /// Enters an invocation and restores the previous value when the scope ends.
    /// </summary>
    public IDisposable EnterFunctionCallContext(FunctionCallContext context)
    {
        var previous = FunctionCallContext.Value;
        var previousCurrent = Harness.FunctionCallContext.EnterCurrent(context);
        FunctionCallContext.Value = context;
        return Disposable.Create(() =>
        {
            FunctionCallContext.Value = previous;
            previousCurrent.Dispose();
        });
    }

    /// <summary>
    /// Suppresses a parent invocation while a nested generation is initialized.
    /// </summary>
    public IDisposable SuppressFunctionCallContext()
    {
        var previous = FunctionCallContext.Value;
        var suppressedCurrent = Harness.FunctionCallContext.SuppressCurrent();
        FunctionCallContext.Value = null;
        return Disposable.Create(() =>
        {
            FunctionCallContext.Value = previous;
            suppressedCurrent.Dispose();
        });
    }

    internal bool TryRetainBackgroundHold()
    {
        while (true)
        {
            var current = Volatile.Read(ref _activeHolders);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _activeHolders, current + 1, current) == current)
            {
                return true;
            }
        }
    }

    internal ValueTask ReleaseBackgroundHoldAsync() => ReleaseHoldAsync();

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return ValueTask.CompletedTask;
        return ReleaseHoldAsync();
    }

    private async ValueTask ReleaseHoldAsync()
    {
        if (Interlocked.Decrement(ref _activeHolders) != 0) return;

        if (Services is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }

        Session.Dispose();
    }
}

/// <summary>
/// Shares only the effective approval mode between a turn and its children. Mode reads are
/// synchronized because invocation entry runs on workers while immediate changes originate in UI.
/// </summary>
public sealed class GenerationApprovalState(ToolApprovalMode mode) : ObservableObject
{
    /// <summary>
    /// Gets or changes the mode for future invocations in this turn and its children.
    /// </summary>
    public ToolApprovalMode Mode
    {
        get => (ToolApprovalMode)Volatile.Read(ref _mode);
        set
        {
            if (Interlocked.Exchange(ref _mode, (int)value) != (int)value) OnPropertyChanged();
        }
    }

    private int _mode = (int)mode;
}
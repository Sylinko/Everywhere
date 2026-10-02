using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Chat.Permissions;
using Microsoft.SemanticKernel;

namespace Everywhere.Chat;

/// <summary>
/// Owns one generation's model resources, approval state, and ambient tool invocation.
/// </summary>
/// <remarks>
/// ChatService publishes this context after initialization and clears it in the same generation's
/// finally block before disposing the mixin. A child shares only ApprovalState; its kernel, mixin,
/// and ambient invocation remain independent. Manual compaction sets IsConversationTurn to false.
/// </remarks>
public sealed class GenerationContext(
    Kernel kernel,
    KernelMixin kernelMixin,
    IPromptRenderer promptRenderer,
    string systemPrompt,
    Modalities inputModalities,
    int contextCompressionThreshold,
    int maxContextRounds,
    GenerationApprovalState approvalState,
    bool isConversationTurn
)
{
    /// <summary>Gets the kernel built for this generation.</summary>
    public Kernel Kernel { get; } = kernel;

    /// <summary>Gets the executing assistant's immutable configuration and provider connection.</summary>
    public KernelMixin KernelMixin { get; } = kernelMixin;

    /// <summary>Gets the renderer scoped to the generation's prompt placeholders.</summary>
    public IPromptRenderer PromptRenderer { get; } = promptRenderer;

    /// <summary>Gets the rendered execution constraints supplied to the executing assistant.</summary>
    public string SystemPrompt { get; } = systemPrompt;

    /// <summary>Gets the accepted input modalities from the executing configuration snapshot.</summary>
    public Modalities InputModalities { get; } = inputModalities;

    /// <summary>Gets the configured automatic compression threshold percentage.</summary>
    public int ContextCompressionThreshold { get; } = contextCompressionThreshold;

    /// <summary>Gets the configured history round limit.</summary>
    public int MaxContextRounds { get; } = maxContextRounds;

    /// <summary>Gets the mode state shared with ordinary children of this turn.</summary>
    public GenerationApprovalState ApprovalState { get; } = approvalState;

    /// <summary>Gets the mode that the next invocation will capture.</summary>
    public ToolApprovalMode ApprovalMode => ApprovalState.Mode;

    /// <summary>Gets whether this context belongs to a conversational generation rather than manual compaction.</summary>
    public bool IsConversationTurn { get; } = isConversationTurn;

    /// <summary>Isolates ambient invocation services within this generation's asynchronous flows.</summary>
    public AsyncLocal<FunctionCallContext?> FunctionCallContext { get; } = new();

    /// <summary>Enters an invocation and restores the previous value when the scope ends.</summary>
    public IDisposable EnterFunctionCallContext(FunctionCallContext context)
    {
        var previous = FunctionCallContext.Value;
        FunctionCallContext.Value = context;
        return Disposable.Create(() => FunctionCallContext.Value = previous);
    }

    /// <summary>Suppresses a parent invocation while a nested generation is initialized.</summary>
    public IDisposable SuppressFunctionCallContext()
    {
        var previous = FunctionCallContext.Value;
        FunctionCallContext.Value = null;
        return Disposable.Create(() => FunctionCallContext.Value = previous);
    }
}

/// <summary>
/// Shares only the effective approval mode between a turn and its children. Mode reads are
/// synchronized because invocation entry runs on workers while immediate changes originate in UI.
/// </summary>
public sealed class GenerationApprovalState(ToolApprovalMode mode) : ObservableObject
{
    /// <summary>Gets or changes the mode for future invocations in this turn and its children.</summary>
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
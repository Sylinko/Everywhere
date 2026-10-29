using System.Collections.Concurrent;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Utilities;
using Everywhere.Views;

namespace Everywhere.Chat;

/// <summary>
/// Owns the runtime of one conversation. Data and serialization have independent identities;
/// nested contexts borrow their root's lifetime and shared environment.
/// </summary>
public abstract partial class ChatContext(
    ChatData data,
    ToolPatternRulesets? toolPatternRulesets,
    GenerationApprovalState? inheritedApprovalState
) : ObservableObject
{
    /// <summary>
    /// Gets the canonical conversation data. Constructing a runtime never clones its nodes.
    /// </summary>
    public ChatData Data { get; } = data;

    /// <summary>
    /// Gets the root owning this context's execution domain and shared resources.
    /// </summary>
    public abstract RootChatContext Root { get; }

    /// <summary>
    /// Gets the common working directory for this root and every nested context.
    /// </summary>
    public abstract string WorkingDirectory { get; }

    /// <summary>
    /// Gets remembered session decisions shared by this context tree.
    /// </summary>
    public ConcurrentDictionary<string, bool> ToolBypassApprovalRulesets => Root.SharedApprovalRules;

    /// <summary>
    /// Gets this context's tool restrictions, independently of its parent's generation.
    /// </summary>
    public ToolPatternRulesets? ToolPatternRulesets { get; } = toolPatternRulesets;

    /// <summary>
    /// Gets the shared interaction broker. Nested contexts never dispose it.
    /// </summary>
    public IChatPluginUserInterfaceBroker UserInterfaceBroker => Root.SharedUserInterfaceBroker;

    /// <summary>
    /// Gets this conversation's remote visual state, which is not shared with its siblings.
    /// </summary>
    public ChatVisualState VisualState { get; } = new();

    /// <summary>
    /// Gets the latest measured context usage for this conversation.
    /// </summary>
    public ContextUsageState ContextUsage { get; } = new();

    /// <summary>
    /// Gets this conversation's compaction state.
    /// </summary>
    public ContextCompactionState ContextCompaction { get; } = new();

    /// <summary>
    /// Gets the current generation, whose lifetime is shorter than the context's lifetime.
    /// </summary>
    [ObservableProperty]
    public partial GenerationContext? GenerationContext { get; internal set; }

    /// <summary>
    /// Gets the approval state inherited from the generation that created this nested context.
    /// </summary>
    public GenerationApprovalState? InheritedApprovalState { get; } = inheritedApprovalState;

    /// <summary>
    /// Gets whether this conversation currently owns an active foreground operation.
    /// </summary>
    public bool IsBusy => Root.HasOperation(this);

    /// <summary>
    /// Gets the stable UI-thread-owned projection. Runtime work never creates it implicitly.
    /// Views borrow the projection; detaching a view does not release it.
    /// </summary>
    public ChatPresentation Presentation
    {
        get
        {
            Dispatcher.UIThread.VerifyAccess();
            ObjectDisposedException.ThrowIf(Data.IsDisposed, this);
            return _presentation ??= new ChatPresentation(this);
        }
    }

    private ChatPresentation? _presentation;

    /// <summary>
    /// Accepts foreground work, retaining it until its final model updates have completed.
    /// Rejected work does not transfer the caller's message or attachment ownership.
    /// </summary>
    public bool TryExecute(Func<CancellationToken, Task> action, IExceptionHandler exceptionHandler)
    {
        if (!Root.TryStartOperation(this, action, CancellationToken.None, out var operation))
        {
            return false;
        }

        operation.Detach(exceptionHandler);
        return true;
    }

    /// <summary>
    /// Runs foreground work and awaits its cleanup without involving the UI dispatcher.
    /// </summary>
    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Root.TryStartOperation(this, action, cancellationToken, out var operation))
        {
            throw new InvalidOperationException("The conversation cannot start another operation.");
        }

        return operation;
    }

    /// <summary>
    /// Requests cancellation of this conversation's current foreground operation.
    /// The context remains available after that operation has finished its cleanup.
    /// </summary>
    public void Cancel() => Root.CancelOperation(this);

    /// <summary>
    /// Registers background work that can outlive a foreground response, such as title generation.
    /// Root shutdown still cancels and awaits that work.
    /// </summary>
    public Task RunBackgroundAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        return Root.StartBackgroundOperation(action, cancellationToken);
    }

    /// <summary>
    /// Creates a nested runtime sharing this root's directory, approvals, and interaction broker.
    /// </summary>
    public NestedChatContext CreateNested(string topic)
    {
        var data = new ChatData();
        data.Metadata.Topic = topic;
        data.Metadata.IsTemporary = true;
        return CreateNested(data);
    }

    private NestedChatContext CreateNested(ChatData data)
    {
        var rules = ToolPatternRulesets.TryUnion(
            new ToolPatternRulesets(2)
            {
                {
                    "builtin.essential",
                    new ToolFunctionPatternRulesets
                    {
                        { "run_subagent", false },
                        { "ask_user_question", false }
                    }
                }
            });
        return Root.CreateNested(this, data, rules, GenerationContext?.ApprovalState ?? InheritedApprovalState);
    }

    /// <summary>
    /// Applies a user branch choice without treating it as a generation. Its short mutation is
    /// retained through completion and cannot be admitted after the root has stopped.
    /// </summary>
    public Task SelectChildAsync(ChatMessageNode parent, ChatMessageNode? child)
    {
        return Root.ApplyAsync(() => Data.SelectChild(parent, child));
    }

    internal void NotifyOperationChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        SetMetadataState(ChatContextMetadataStates.Busy, IsBusy);
    }

    internal void SetMetadataState(ChatContextMetadataStates state, bool isEnabled)
    {
        var states = isEnabled ? Data.Metadata.States | state : Data.Metadata.States & ~state;
        if (states == Data.Metadata.States)
        {
            return;
        }

        Data.Metadata.States = states;
        WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(Data.Metadata, nameof(ChatMetadata.States)));
    }

    /// <summary>
    /// Applies a short model update in the root's shared serial execution domain. The delegate
    /// must not wait for I/O or UI work. Accepted operations use this for their final updates too.
    /// </summary>
    public Task DispatchAsync(Action action) => Root.EnqueueAsync(action);

    /// <summary>
    /// Captures or updates model values in the same domain as parent and nested conversation writes.
    /// The delegate is synchronous; return detached values before performing asynchronous I/O.
    /// </summary>
    public Task<T> DispatchAsync<T>(Func<T> action) => Root.EnqueueAsync(action);

    /// <summary>
    /// Publishes measured usage independently of whether a view is attached.
    /// </summary>
    public Task ReportContextUsageAsync(ChatUsageDetails usage, string? modelId, int contextLimit)
    {
        return DispatchAsync(() => ContextUsage.Report(usage, modelId, contextLimit));
    }

    /// <summary>
    /// Records successful compaction without waiting for a UI projection.
    /// </summary>
    public Task MarkContextCompactedAsync(string? modelId, int contextLimit)
    {
        return DispatchAsync(() => ContextUsage.MarkCompacted(modelId, contextLimit));
    }

    /// <summary>
    /// Updates the model used to interpret this context's latest usage snapshot.
    /// </summary>
    public Task UpdateContextUsageModelAsync(string? modelId, int contextLimit)
    {
        return DispatchAsync(() => ContextUsage.UpdateModel(modelId, contextLimit));
    }

    /// <summary>
    /// Owns compaction state until normal or canceled cleanup has finished in the execution domain.
    /// </summary>
    public async Task<IAsyncDisposable> BeginContextCompactionAsync()
    {
        await DispatchAsync(ContextCompaction.Start).ConfigureAwait(false);
        return new CompactionScope(this);
    }

    private sealed class CompactionScope(ChatContext context) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(context.DispatchAsync(context.ContextCompaction.Finish));
    }

    internal void ReleaseResources()
    {
        VisualState.Dispose();
        // Data is already disposed, so no new projection can be requested. Only the UI callback
        // accesses the field; shutdown never waits for UI cleanup or takes a projection lock.
        Dispatcher.UIThread.PostOnDemand(() => DisposeHelper.DisposeToDefault(ref _presentation));
    }
}
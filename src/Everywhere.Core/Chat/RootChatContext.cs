using System.Collections.Concurrent;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Chat;

/// <summary>
/// Owns a root conversation and the work of all its nested contexts. The manager closes roots
/// before releasing storage. Stopping and draining work never requires an Avalonia dispatcher.
/// </summary>
public sealed partial class RootChatContext : ChatContext, IAsyncDisposable
{
    /// <inheritdoc />
    public override RootChatContext Root => this;

    /// <inheritdoc />
    public override string WorkingDirectory { get; }

    internal IChatTurnLoader? Loader { get; }

    internal ConcurrentDictionary<string, bool> SharedApprovalRules { get; } = new();

    internal ChatPluginUserInterfaceBroker SharedUserInterfaceBroker { get; }

    // The manager indexes this same reference. Only accepted work promotes it; views and
    // pending saves retain the context through their own ordinary strong references.
    internal ResilientReference<RootChatContext> Reference { get; }

    internal bool HasWork
    {
        get
        {
            using var scope = _lifetimeLock.EnterScope();
            return _operations.Count != 0;
        }
    }

    /// <summary>
    /// Raised after all foreground, nested, background, and cancellation cleanup work ends.
    /// The manager uses this boundary for final persistence and stable sync publication.
    /// </summary>
    public event EventHandler? Idle;

    private readonly Lock _lifetimeLock = new();
    private readonly Dictionary<ChatContext, Operation> _foregroundOperations = new();
    private readonly HashSet<Operation> _operations = [];
    private readonly Dictionary<ChatData, NestedChatContext> _children = new(ReferenceEqualityComparer.Instance);

    private CancellationTokenSource _lifetimeCancellation = new();
    private bool _isStopping;
    private Task? _disposeTask;
    private Task _workAdmission = Task.CompletedTask;

    /// <summary>
    /// Attaches a new runtime to canonical data without starting work or creating a presentation.
    /// </summary>
    internal RootChatContext(ChatData data, IChatTurnLoader? loader) : base(data, null, null)
    {
        Loader = loader;
        Reference = new ResilientReference<RootChatContext>(this, isActive: false);
        WorkingDirectory = RuntimeConstants.EnsureWritableDataFolderPath("plugins", data.Metadata.DateCreated.ToString("yyyy-MM-dd"));
        SharedUserInterfaceBroker = new ChatPluginUserInterfaceBroker(SetHasNotification);
        foreach (var node in data.GetAllNodes().Where(node => node.IsMessageLoaded))
        {
            AttachNestedContexts(node.Message);
        }
    }

    internal bool HasOperation(ChatContext context)
    {
        using var scope = _lifetimeLock.EnterScope();
        return _foregroundOperations.ContainsKey(context);
    }

    internal bool TryStartOperation(
        ChatContext context,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken,
        out Task task)
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_isStopping || Data.Metadata.IsArchived || _disposeTask is not null || Data.IsDisposed || context.Data.IsDisposed ||
            _foregroundOperations.ContainsKey(context))
        {
            task = Task.CompletedTask;
            return false;
        }

        var operation = new Operation(_lifetimeCancellation.Token, cancellationToken);
        _operations.Add(operation);
        Reference.IsActive = true;
        _foregroundOperations.Add(context, operation);
        var admission = _workAdmission;
        RunOperationAsync(context, operation, token => RunAfterAdmissionAsync(admission, action, token)).Detach();
        task = operation.Completion.Task;
        return true;
    }

    internal Task StartBackgroundOperation(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_isStopping || _disposeTask is not null)
        {
            return Task.FromCanceled(new CancellationToken(true));
        }

        var operation = new Operation(_lifetimeCancellation.Token, cancellationToken);
        _operations.Add(operation);
        Reference.IsActive = true;
        var admission = _workAdmission;
        RunOperationAsync(null, operation, token => RunAfterAdmissionAsync(admission, action, token)).Detach();
        return operation.Completion.Task;
    }

    /// <summary>
    /// Accepts a short external mutation and queues it immediately, preserving the caller's order.
    /// Unlike internal DispatchAsync calls, admission closes when this root stops.
    /// </summary>
    internal Task ApplyAsync(Action mutation)
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_isStopping || _disposeTask is not null)
        {
            return Task.FromCanceled(new CancellationToken(true));
        }

        var operation = new Operation(_lifetimeCancellation.Token, CancellationToken.None);
        _operations.Add(operation);
        Reference.IsActive = true;
        var shouldWait = !_workAdmission.IsCompleted;
        var pending = shouldWait ? ApplyAfterAdmissionAsync(_workAdmission, mutation) : EnqueueAsync(mutation);
        if (shouldWait)
        {
            // Preserve external mutation order across the work admission boundary,
            // including calls arriving after commit but before earlier queued mutations resume.
            _workAdmission = pending;
        }

        RunOperationAsync(null, operation, _ => pending).Detach();
        return operation.Completion.Task;
    }

    private async Task RunOperationAsync(ChatContext? context, Operation operation, Func<CancellationToken, Task> action)
    {
        Exception? failure = null;
        try
        {
            if (context is not null)
            {
                await DispatchAsync(context.NotifyOperationChanged).ConfigureAwait(false);
            }

            // Accepted work enters its normal cleanup path even if cancellation precedes scheduling.
            await action(operation.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (context is not null)
                {
                    await FinishForegroundAsync(context, operation).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
            finally
            {
                bool isIdle;
                using (_lifetimeLock.EnterScope())
                {
                    operation.Cancellation.Dispose();
                    _operations.Remove(operation);
                    isIdle = _operations.Count == 0;
                    Reference.IsActive = !isIdle;
                    if (failure is OperationCanceledException canceled)
                    {
                        operation.Completion.TrySetCanceled(canceled.CancellationToken);
                    }
                    else if (failure is not null)
                    {
                        operation.Completion.TrySetException(failure);
                    }
                    else
                    {
                        operation.Completion.TrySetResult();
                    }
                }
                if (isIdle)
                {
                    Idle?.Invoke(this, EventArgs.Empty);
                }
            }
        }
    }

    /// <summary>
    /// Under the manager's commit gate, reserve an idle model for a detached capture or
    /// received commit. Complete the returned signal to let queued work enter in order.
    /// </summary>
    internal TaskCompletionSource? TryDeferWorkWhenIdle()
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_operations.Count != 0 || _isStopping || _disposeTask is not null)
        {
            return null;
        }
        var admission = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _workAdmission = admission.Task;
        return admission;
    }

    private static async Task RunAfterAdmissionAsync(Task admission, Func<CancellationToken, Task> action, CancellationToken token)
    {
        try
        {
            // Admission protects a committed publication, not whether already accepted work
            // exists. Enter the action even when canceled so the send path retains user input
            // and runs its ordinary cleanup before observing the canceled token.
            await admission.ConfigureAwait(false);
        }
        catch
        {
            // A preceding short mutation owns its failure; this independently accepted work still runs.
        }
        await Task.Run(() => action(token), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ApplyAfterAdmissionAsync(Task admission, Action mutation)
    {
        try
        {
            await admission.ConfigureAwait(false);
        }
        catch
        {
            // The preceding operation reports its own error; it must not cancel this independent mutation.
        }
        await EnqueueAsync(mutation).ConfigureAwait(false);
    }

    private async Task FinishForegroundAsync(ChatContext context, Operation operation)
    {
        Task? cancellation;
        using (_lifetimeLock.EnterScope())
        {
            cancellation = operation.CancellationCompletion;
            if (cancellation is null)
            {
                // Completion wins admission against a late Cancel call. Once removed, this
                // operation cannot acquire new cancellation callbacks while being disposed.
                _foregroundOperations.Remove(context);
            }
        }

        try
        {
            if (cancellation is not null)
            {
                await cancellation.ConfigureAwait(false);
            }
        }
        finally
        {
            if (cancellation is not null)
            {
                using var scope = _lifetimeLock.EnterScope();
                _foregroundOperations.Remove(context);
            }

            await DispatchAsync(context.NotifyOperationChanged).ConfigureAwait(false);
        }
    }

    private void SetHasNotification(bool hasNotification)
    {
        DispatchAsync(() => SetMetadataState(ChatContextMetadataStates.HasNotification, hasNotification)).Detach();
    }

    internal void CancelOperation(ChatContext context)
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_foregroundOperations.TryGetValue(context, out var operation))
        {
            operation.RequestCancellation();
        }
    }

    /// <summary>
    /// Resolves an embedded conversation already attached to this root, without creating a runtime
    /// or searching another root. The owning presentation uses this when opening a subagent view.
    /// </summary>
    public NestedChatContext GetNested(ChatData data)
    {
        using var scope = _lifetimeLock.EnterScope();
        return _children[data];
    }

    internal bool HasNested(ChatData data)
    {
        using var scope = _lifetimeLock.EnterScope();
        return _children.ContainsKey(data);
    }

    internal void DetachNestedContexts(ChatMessage message)
    {
        foreach (var data in ChatMessageResources.GetChildData(message))
        {
            using var scope = _lifetimeLock.EnterScope();
            if (!_children.Remove(data, out var child))
            {
                continue;
            }
            foreach (var node in data.GetAllNodes().Where(node => node.IsMessageLoaded))
            {
                DetachNestedContexts(node.Message);
            }
            child.ReleaseResources();
        }
    }

    internal NestedChatContext CreateNested(
        ChatContext parent,
        ChatData data,
        ToolPatternRulesets? rules,
        GenerationApprovalState? approvalState)
    {
        using var scope = _lifetimeLock.EnterScope();
        // Registering an embedded runtime does not admit agent work. Loading and received
        // commits must finish attaching their already-owned data while StopAsync drains work.
        // Foreground/background admission remains closed until ResumeAfterStop.
        ObjectDisposedException.ThrowIf(Data.IsDisposed, this);
        var child = new NestedChatContext(parent, data, rules, approvalState);
        _children.Add(data, child);
        foreach (var node in data.GetAllNodes().Where(node => node.IsMessageLoaded))
        {
            child.AttachNestedContexts(node.Message);
        }

        return child;
    }

    /// <summary>
    /// Stops accepting operations, cancels all owned work, and awaits its final model updates.
    /// Repeated callers may safely await the same outstanding operations.
    /// </summary>
    public async Task StopAsync()
    {
        Task[] operations;
        Task cancellation;
        using (_lifetimeLock.EnterScope())
        {
            if (_disposeTask?.IsCompleted is true)
            {
                return;
            }

            _isStopping = true;
            operations = _operations.Select(operation => operation.Completion.Task).ToArray();
            cancellation = _lifetimeCancellation.CancelAsync();
        }

        try
        {
            await cancellation.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Log.ForContext<RootChatContext>().Warning(exception, "A chat cancellation callback failed.");
        }

        try
        {
            await Task.WhenAll(operations).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.ForContext<RootChatContext>().Warning(exception, "Chat work failed while stopping.");
        }
    }

    internal void BeginDeletion()
    {
        using var scope = _lifetimeLock.EnterScope();
        if (_foregroundOperations.Count > 0)
        {
            throw new InvalidOperationException("A running conversation cannot be permanently deleted.");
        }

        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        _isStopping = true;
    }

    internal void ResumeAfterStop()
    {
        using var scope = _lifetimeLock.EnterScope();
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        _lifetimeCancellation.Dispose();
        _lifetimeCancellation = new CancellationTokenSource();
        _isStopping = false;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        using var scope = _lifetimeLock.EnterScope();
        _disposeTask ??= DisposeCoreAsync();
        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await DispatchAsync(() =>
        {
            foreach (var child in _children.Values)
            {
                child.Data.Dispose();
                child.ReleaseResources();
            }

            _children.Clear();
            SharedUserInterfaceBroker.Dispose();
            Data.Dispose();
            ReleaseResources();
            _lifetimeCancellation.Dispose();
        }).ConfigureAwait(false);
        _execution.Complete();
        await _execution.Completion.ConfigureAwait(false);
    }

    private sealed class Operation(CancellationToken lifetimeToken, CancellationToken parentToken)
    {
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, parentToken);

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task? CancellationCompletion { get; private set; }

        public void RequestCancellation()
        {
            CancellationCompletion ??= Cancellation.CancelAsync();
        }
    }
}

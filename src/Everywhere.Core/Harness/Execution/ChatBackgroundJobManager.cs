using System.Diagnostics.CodeAnalysis;
using System.Text;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Terminal;

namespace Everywhere.Harness;

/// <summary>
/// Tracks one detached tool invocation running in the background of a <see cref="RootChatContext"/>.
/// </summary>
public sealed class ChatBackgroundJob
{
    /// <summary>
    /// Gets the short session-unique background job identifier (e.g. <c>bg-1</c>).
    /// </summary>
    public string JobId { get; }

    /// <summary>
    /// Gets the logical tool name (e.g. <c>execute_in_terminal</c>, <c>run_subagent</c>).
    /// </summary>
    public string ToolName { get; }

    /// <summary>
    /// Gets the human-readable job label shown in the UI tray and job list.
    /// </summary>
    public string Label { get; }

    /// <summary>
    /// Gets the conversation context that owns this job's wake notifications.
    /// </summary>
    public ChatContext OwnerContext { get; }

    /// <summary>
    /// Gets the explicit user turn that started this detached job, or <see langword="null"/> if none.
    /// </summary>
    public ChatTurn? OwnerTurn { get; }

    /// <summary>
    /// Gets the function call message whose display blocks receive live updates from this job.
    /// </summary>
    public FunctionCallChatMessage? SourceMessage { get; }

    /// <summary>
    /// Gets when the background job was detached.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Gets when the job finished, or <see langword="null"/> while running.
    /// </summary>
    public DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>
    /// Gets whether execution has finished.
    /// </summary>
    public bool IsCompleted { get; private set; }

    /// <summary>
    /// Gets whether the job completed without throwing an error.
    /// </summary>
    public bool IsSuccess { get; private set; }

    /// <summary>
    /// Gets the final string output once completed.
    /// </summary>
    public string? Output { get; private set; }

    /// <summary>
    /// Gets the error message when <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    public string? ErrorMessage { get; private set; }

    private readonly CancellationTokenSource _stopCts;
    private readonly Action<ChatBackgroundJob>? _onStopRequested;
    private int _turnHoldReleased;
    private int _isAutoWakeSuppressed;
    private int _isStopRequested;

    /// <summary>
    /// Gets whether cooperative cancellation has been requested for this background job.
    /// </summary>
    public bool IsStopRequested => Volatile.Read(ref _isStopRequested) != 0;

    /// <summary>
    /// Gets whether completion of this job should record its result without automatically waking the model.
    /// </summary>
    public bool IsAutoWakeSuppressed => Volatile.Read(ref _isAutoWakeSuppressed) != 0;

    /// <summary>
    /// Initializes a tracked background job.
    /// </summary>
    public ChatBackgroundJob(
        string jobId,
        string toolName,
        string label,
        ChatContext ownerContext,
        ChatTurn? ownerTurn,
        FunctionCallChatMessage? sourceMessage,
        CancellationTokenSource stopCts,
        Action<ChatBackgroundJob>? onStopRequested = null)
    {
        JobId = jobId;
        ToolName = toolName;
        Label = label;
        OwnerContext = ownerContext;
        OwnerTurn = ownerTurn;
        SourceMessage = sourceMessage;
        StartedAt = DateTimeOffset.UtcNow;
        _stopCts = stopCts;
        _onStopRequested = onStopRequested;
    }

    internal void ReleaseTurnHold()
    {
        if (Interlocked.Exchange(ref _turnHoldReleased, 1) != 0)
        {
            return;
        }

        OwnerTurn?.DecrementActiveBackgroundJobs();
        OwnerContext.DispatchAsync(OwnerContext.NotifyTurnStateChanged).Detach();
    }

    /// <summary>
    /// Requests cooperative cancellation of this background job.
    /// </summary>
    public void RequestStop(bool shouldSuppressAutoWake = false)
    {
        if (shouldSuppressAutoWake)
        {
            Interlocked.Exchange(ref _isAutoWakeSuppressed, 1);
        }

        if (IsCompleted) return;
        if (Interlocked.Exchange(ref _isStopRequested, 1) == 0)
        {
            OwnerTurn?.Invalidate();
        }

        _onStopRequested?.Invoke(this);
        try
        {
            _stopCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already completed and disposed.
        }
    }

    /// <summary>
    /// Captures either the final output or live streaming progress from the attached display blocks.
    /// </summary>
    public string ReadLiveOutput()
    {
        if (IsCompleted)
        {
            if (IsSuccess) return Output ?? string.Empty;
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(ErrorMessage))
            {
                builder.AppendLine($"Error: {ErrorMessage}");
            }

            if (!string.IsNullOrWhiteSpace(Output))
            {
                builder.Append(Output);
            }

            return builder.ToString().Trim();
        }

        if (SourceMessage is null) return string.Empty;

        var live = new StringBuilder();
        var hasEarlierOmission = false;
        var hasTerminalBlock = false;
        foreach (var block in SourceMessage.DisplayBlocks)
        {
            var text = block.ReadLiveOutput();
            if (string.IsNullOrWhiteSpace(text)) continue;

            if (block is ChatPluginTerminalDisplayBlock)
            {
                hasTerminalBlock = true;
                text = TerminalLineBuffer.StripOmissionPrefix(text, out var blockOmitted);
                if (blockOmitted)
                {
                    hasEarlierOmission = true;
                }
            }

            var trimmedText = text.TrimEnd();
            if (trimmedText.Length == 0) continue;

            if (live.Length > 0)
            {
                live.Append('\n');
            }

            live.Append(trimmedText);
        }

        if (live.Length == 0)
        {
            return hasEarlierOmission ? "[earlier output omitted]" : string.Empty;
        }

        var combined = live.ToString().Trim();
        return hasTerminalBlock || hasEarlierOmission
            ? TerminalLineBuffer.TruncateTail(combined, TerminalLineBuffer.DefaultPreviewLength, hasEarlierOmission)
            : combined;
    }

    internal void MarkCompleted(bool isSuccess, string? output, string? errorMessage)
    {
        IsSuccess = isSuccess;
        Output = output;
        ErrorMessage = errorMessage;
        FinishedAt = DateTimeOffset.UtcNow;
        IsCompleted = true;
    }
}

/// <summary>
/// Manages detached long-running tool invocations owned by one <see cref="RootChatContext"/>.
/// </summary>
public sealed class ChatBackgroundJobManager : IDisposable
{
    /// <summary>
    /// Gets a snapshot of currently running background jobs in registration order.
    /// </summary>
    public IReadOnlyList<ChatBackgroundJob> ActiveJobs
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _orderedJobs.AsValueEnumerable().Where(static j => !j.IsCompleted).ToArray();
        }
    }

    private readonly RootChatContext _root;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, ChatBackgroundJob> _jobsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChatBackgroundJob> _orderedJobs = [];

    private int _nextJobNumber;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a background job manager owned by <paramref name="root"/>.
    /// </summary>
    public ChatBackgroundJobManager(RootChatContext root)
    {
        _root = root;
    }

    /// <summary>
    /// Returns the currently running background jobs owned by <paramref name="turn"/> in registration order.
    /// </summary>
    public IReadOnlyList<ChatBackgroundJob> GetActiveTurnJobs(ChatTurn turn)
    {
        using var scope = _sync.EnterScope();
        return _orderedJobs.AsValueEnumerable().Where(j => !j.IsCompleted && ReferenceEquals(j.OwnerTurn, turn)).ToArray();
    }

    /// <summary>
    /// Registers an already-running detached tool task as a background job retained under <see cref="RootChatContext"/>.
    /// </summary>
    public ChatBackgroundJob RegisterDetachedJob(
        ChatContext ownerContext,
        ChatTurn? ownerTurn,
        string toolName,
        string label,
        FunctionCallChatMessage? sourceMessage,
        Task<(ToolResultRecord Record, Exception? Error)> executionTask,
        CancellationTokenSource stopCts,
        Action<ChatBackgroundJob> onCompleted,
        Action<ChatBackgroundJob>? onStopRequested = null)
    {
        var jobNumber = Interlocked.Increment(ref _nextJobNumber);
        var jobId = $"bg-{jobNumber}";
        var job = new ChatBackgroundJob(
            jobId,
            toolName,
            string.IsNullOrWhiteSpace(label) ? toolName : label,
            ownerContext,
            ownerTurn,
            sourceMessage,
            stopCts,
            onStopRequested);

        using (_sync.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _jobsById.Add(jobId, job);
            _orderedJobs.Add(job);
        }

        ownerTurn?.IncrementActiveBackgroundJobs();
        ownerContext.DispatchAsync(ownerContext.NotifyTurnStateChanged).Detach();

        _root.StartBackgroundOperation(
                async rootToken =>
                {
                    await using var linkedStop = rootToken.Register(() => job.RequestStop());
                    var isSuccess = false;
                    string? errorMessage = null;
                    string? output;
                    try
                    {
                        var result = await executionTask.ConfigureAwait(false);
                        var content = result.Record.Result as ToolContentResult;
                        output = content?.FormatNotificationText(result.Record.Files) ?? result.Record.Result?.ToString();
                        if (result.Error is { } error)
                        {
                            errorMessage = stopCts.IsCancellationRequested ? "Stopped." : error.Message;
                            var partial = job.ReadLiveOutput();
                            if (!string.IsNullOrWhiteSpace(partial) || stopCts.IsCancellationRequested)
                            {
                                output = partial;
                            }
                        }
                        else
                        {
                            isSuccess = content?.IsError is not true;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        errorMessage = "Stopped.";
                        output = job.ReadLiveOutput();
                    }
                    catch (Exception ex)
                    {
                        errorMessage = ex.Message;
                        output = job.ReadLiveOutput();
                    }
                    finally
                    {
                        stopCts.Dispose();
                    }

                    job.MarkCompleted(isSuccess, output, errorMessage);

                    try
                    {
                        if (!rootToken.IsCancellationRequested)
                        {
                            onCompleted(job);
                        }
                    }
                    finally
                    {
                        job.ReleaseTurnHold();
                    }
                },
                CancellationToken.None)
            .Detach();

        return job;
    }

    /// <summary>
    /// Gets whether any detached background job in this session is still running.
    /// </summary>
    public bool HasActiveJobs
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _orderedJobs.AsValueEnumerable().Any(static j => !j.IsCompleted);
        }
    }

    /// <summary>
    /// Returns a snapshot of all jobs tracked during this session (running and completed).
    /// </summary>
    public IReadOnlyList<ChatBackgroundJob> ListJobs()
    {
        using var scope = _sync.EnterScope();
        return [.. _orderedJobs];
    }

    /// <summary>
    /// Looks up a job by identifier.
    /// </summary>
    public bool TryGetJob(string jobId, [NotNullWhen(true)] out ChatBackgroundJob? job)
    {
        using var scope = _sync.EnterScope();
        return _jobsById.TryGetValue(jobId, out job);
    }

    /// <summary>
    /// Requests cancellation of the specified job if it exists and is still running.
    /// </summary>
    public bool TryStopJob(string jobId)
    {
        ChatBackgroundJob? job;
        using (_sync.EnterScope())
        {
            if (!_jobsById.TryGetValue(jobId, out job)) return false;
        }

        if (job.IsCompleted) return false;
        job.RequestStop();
        return true;
    }

    /// <summary>
    /// Stops running background jobs belonging to <paramref name="context"/> (or all session jobs when <paramref name="context"/> is the root).
    /// </summary>
    public void StopContextJobs(ChatContext context, bool shouldSuppressAutoWake = false)
    {
        var isRoot = ReferenceEquals(context, _root);
        ChatBackgroundJob[] running;
        using (_sync.EnterScope())
        {
            running = _orderedJobs.AsValueEnumerable().Where(j => !j.IsCompleted && (isRoot || ReferenceEquals(j.OwnerContext, context))).ToArray();
        }

        foreach (var job in running)
        {
            job.RequestStop(shouldSuppressAutoWake);
        }
    }

    /// <summary>
    /// Stops all currently running background jobs in this session.
    /// </summary>
    public void StopAll()
    {
        ChatBackgroundJob[] running;
        using (_sync.EnterScope())
        {
            running = _orderedJobs.Where(static j => !j.IsCompleted).ToArray();
        }

        foreach (var job in running)
        {
            job.RequestStop();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        using (_sync.EnterScope())
        {
            if (_isDisposed) return;
            _isDisposed = true;
            foreach (var job in _orderedJobs)
            {
                job.RequestStop();
            }

            _jobsById.Clear();
            _orderedJobs.Clear();
        }
    }
}

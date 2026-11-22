using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables.Fluent;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Terminal;

namespace Everywhere.Harness;

/// <summary>
/// Tracks one detached tool invocation running in the background of a <see cref="RootChatContext"/>.
/// </summary>
public sealed partial class ChatBackgroundJob : ObservableObject
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
    [ObservableProperty]
    public partial DateTimeOffset? FinishedAt { get; private set; }

    /// <summary>
    /// Gets whether execution has finished.
    /// </summary>
    [ObservableProperty]
    public partial bool IsCompleted { get; private set; }

    /// <summary>
    /// Gets whether the job completed without throwing an error.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSuccess { get; private set; }

    /// <summary>
    /// Gets the final string output once completed.
    /// </summary>
    [ObservableProperty]
    public partial string? Output { get; private set; }

    /// <summary>
    /// Gets the error message when <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    private readonly CancellationTokenSource _stopCts;
    private readonly Action<ChatBackgroundJob>? _onStopRequested;
    private int _turnHoldReleased;

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
        OwnerContext.NotifyTurnStateChanged();
    }

    /// <summary>
    /// Requests cooperative cancellation of this background job.
    /// </summary>
    public void RequestStop()
    {
        if (IsCompleted) return;
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
    /// Gets the UI-thread-observed list of currently running background jobs.
    /// </summary>
    public IReadOnlyBindableList<ChatBackgroundJob> ActiveJobs { get; }

    private readonly RootChatContext _root;
    private readonly Lock _sync = new();
    private readonly Dictionary<string, ChatBackgroundJob> _jobsById = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ChatBackgroundJob> _orderedJobs = [];
    private readonly SourceList<ChatBackgroundJob> _activeJobsSource = new();
    private readonly CompositeDisposable _disposables = new(2);

    private int _nextJobNumber;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a background job manager owned by <paramref name="root"/>.
    /// </summary>
    public ChatBackgroundJobManager(RootChatContext root)
    {
        _root = root;
        ActiveJobs = _activeJobsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _activeJobsSource.DisposeWith(_disposables);
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

        ownerTurn?.IncrementActiveBackgroundJobs();
        using (_sync.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _jobsById.Add(jobId, job);
            _orderedJobs.Add(job);
            _activeJobsSource.Add(job);
        }
        ownerContext.NotifyTurnStateChanged();

        _root.StartBackgroundOperation(
                async rootToken =>
                {
                    await using var linkedStop = rootToken.Register(job.RequestStop);
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
                    using (_sync.EnterScope())
                    {
                        _activeJobsSource.Remove(job);
                    }

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

        _disposables.Dispose();
    }
}

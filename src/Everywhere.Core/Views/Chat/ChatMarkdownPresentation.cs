using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using LiveMarkdown.Avalonia;
using Markdig;
using Markdig.Syntax;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Owns one source's parsed Markdown cache. UI-thread requests coalesce to one active parse and
/// the latest pending source version; successful intermediate versions remain displayable.
/// </summary>
/// <remarks>
/// Refresh, preparation, and disposal are UI-thread-owned. Only parsing a captured text snapshot
/// runs on a worker. Window cancellation stops a caller's wait, not the shared parse.
/// </remarks>
public sealed partial class ChatMarkdownPresentation(ChatTextBuffer source) : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets the latest accepted document. Parsing does not change the chat model or persistence state.
    /// </summary>
    [ObservableProperty]
    public partial MarkdownDocumentUpdate? DocumentUpdate { get; private set; }

    /// <summary>
    /// Prevents excessive refreshes when many tokens arrive in a short time. The first parse of a new version
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(33);

    private Task? _parseTask;
    private DispatcherTimer? _refreshTimer;
    private long _lastCommitTimestamp;
    private int _committedLength;
    private bool _isDisposed;
    private long _epoch = source.Epoch;

    /// <summary>
    /// Requests the latest source value without queuing one parse for every streamed token.
    /// </summary>
    public void Refresh()
    {
        if (_isDisposed)
        {
            return;
        }

        InvalidateReplacedDocument();
        if (DocumentUpdate?.Version == source.Version || _parseTask is { IsCompleted: false } || _refreshTimer is { IsEnabled: true })
        {
            return;
        }

        if (DocumentUpdate is not null)
        {
            var remaining = RefreshInterval - Stopwatch.GetElapsedTime(_lastCommitTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                // Sample the latest snapshot when the timer fires. More tokens do not postpone it.
                if (_refreshTimer is null)
                {
                    _refreshTimer = new DispatcherTimer(DispatcherPriority.Render);
                    _refreshTimer.Tick += delegate
                    {
                        if (!_isDisposed)
                        {
                            GetOrStartParse();
                        }
                    };
                }

                _refreshTimer.Interval = remaining;
                _refreshTimer.Start();
                return;
            }
        }

        GetOrStartParse();
    }

    /// <summary>
    /// Waits for a valid document covering the version observed on entry. Later appends do not
    /// extend this target; clearing the source requires a document from its replacement epoch.
    /// Cancellation affects only this wait, so an already-visible row keeps parsing normally.
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var targetVersion = source.Version;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            InvalidateReplacedDocument();
            if (DocumentUpdate is { } update && update.Version >= targetVersion)
            {
                return;
            }

            await GetOrStartParse().WaitAsync(cancellationToken);
        }
    }

    private void InvalidateReplacedDocument()
    {
        if (_epoch != source.Epoch)
        {
            _epoch = source.Epoch;
            _refreshTimer?.Stop();
            _committedLength = 0;
            DocumentUpdate = null;
        }
    }

    private Task GetOrStartParse()
    {
        // Preparation comes here directly, bypassing any scheduled streaming refresh.
        _refreshTimer?.Stop();
        if (_parseTask is { IsCompleted: false })
        {
            return _parseTask;
        }

        var task = ParseAsync(source.CaptureSnapshot());
        _parseTask = task;
        ObserveParseAsync(task).Detach();
        return task;
    }

    private void Commit(ChatTextSnapshot snapshot, MarkdownDocument document)
    {
        if (_isDisposed || source.Epoch != snapshot.Epoch)
        {
            return;
        }

        var previous = DocumentUpdate;
        if (previous is not null && _epoch == snapshot.Epoch && previous.Version >= snapshot.Version)
        {
            return;
        }

        var start = _committedLength;
        var length = snapshot.Text.Length;
        var isAppend = _epoch == snapshot.Epoch && length >= start;
        _epoch = snapshot.Epoch;
        _committedLength = length;
        _lastCommitTimestamp = Stopwatch.GetTimestamp();
        if (previous is not null && isAppend)
        {
            var change = new ObservableStringBuilderChangedEventArgs(start, length - start, length, snapshot.Version);
            DocumentUpdate = new MarkdownDocumentUpdate.Incremental(previous, document, change);
        }
        else
        {
            DocumentUpdate = new MarkdownDocumentUpdate.Full(document, snapshot.Version);
        }
    }

    private async Task ParseAsync(ChatTextSnapshot snapshot)
    {
        var document = await Task.Run(() => Markdown.Parse(snapshot.Text, MarkdownUpdateProducer.DefaultPipeline)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Commit(snapshot, document));
    }

    private async Task ObserveParseAsync(Task task)
    {
        try
        {
            await task;
            // Each shared task ends after one commit, not after the whole stream catches up.
            // Preheating can finish while this observer requests the next source version.
            Refresh();
        }
        catch (Exception exception)
        {
            // Keep the shared task faulted for its waiters. Do not immediately retry a failed parse.
            Log.Error(exception, "Failed to parse chat Markdown.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _isDisposed = true;
        _refreshTimer?.Stop();
        _committedLength = 0;
        DocumentUpdate = null;
    }
}
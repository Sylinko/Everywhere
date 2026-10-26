using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using LiveMarkdown.Avalonia;
using Markdig;
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

    private Task? _parseTask;
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
        if (DocumentUpdate?.Version == source.Version)
        {
            return;
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
            DocumentUpdate = null;
        }
    }

    private Task GetOrStartParse()
    {
        if (_parseTask is { IsCompleted: false })
        {
            return _parseTask;
        }

        var task = ParseAsync(source.CaptureSnapshot());
        _parseTask = task;
        ObserveParseAsync(task).Detach();
        return task;
    }

    private void Commit(ChatTextSnapshot snapshot, MarkdownDocumentUpdate update)
    {
        if (_isDisposed || source.Epoch != snapshot.Epoch)
        {
            return;
        }

        if (_epoch != snapshot.Epoch || DocumentUpdate is null || DocumentUpdate.Version < update.Version)
        {
            _epoch = snapshot.Epoch;
            DocumentUpdate = update;
        }
    }

    private async Task ParseAsync(ChatTextSnapshot snapshot)
    {
        var document = await Task.Run(() => Markdown.Parse(snapshot.Text, MarkdownUpdateProducer.DefaultPipeline)).ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => Commit(snapshot, new MarkdownDocumentUpdate.Full(document, snapshot.Version)));
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
        DocumentUpdate = null;
    }
}
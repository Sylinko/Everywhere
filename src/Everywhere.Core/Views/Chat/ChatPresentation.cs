using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Markdig;
using Serilog;

namespace Everywhere.Views;

/// <summary>
/// Incrementally projects a contiguous window of the current chat branch into stable, flat
/// presentation rows. Materialized turns and their child row lists retain identity; a positional
/// segmented list applies their DynamicData changes without recreating unaffected rows or controls.
/// </summary>
/// <remarks>
/// The projection is UI-thread-affine. The chat model is intentionally allowed to stream from
/// worker tasks, so notification handlers marshal into the Avalonia dispatcher before touching any
/// row, source list, or subscription state. The branch change-set subscription keeps its own
/// interlocked coalescing flag because that observable preserves the producer's worker thread.
/// </remarks>
public sealed class ChatPresentation : ObservableObject, IDisposable
{
    public const int TurnBatchSize = 8;

    /// <summary>
    /// Gets the stable, flat list consumed by the outer virtualizing chat ItemsControl. Row identity
    /// is retained while its complete turn remains in the materialized window.
    /// </summary>
    public IReadOnlyBindableList<ChatPresentationRow> Rows { get; }

    /// <summary>
    /// Gets whether complete turns exist before the materialized presentation window.
    /// </summary>
    public bool HasEarlierTurns => _windowStart > 0;

    /// <summary>
    /// Gets whether complete turns exist after the materialized presentation window.
    /// </summary>
    public bool HasLaterTurns => _windowEnd < _descriptors.Count;

    /// <summary>
    /// Gets whether the materialized window currently contains the latest turn.
    /// </summary>
    public bool IsAtLatest => !HasLaterTurns;

    public bool IsWindowOperationActive => _windowOperationCancellation is not null;

    private readonly ChatContext _context;
    private readonly SourceList<IChatPresentationSegment> _segments = new();
    private readonly Dictionary<object, ChatTurnPresentation> _turns = new(ReferenceEqualityComparer.Instance);
    private readonly List<BusyActivityItemPresentationRow> _busyActivities = [];
    private readonly DynamicSegmentedList<IChatPresentationSegment, ChatPresentationRow> _visibleRows;
    private readonly CompositeDisposable _disposables = new();
    private List<TurnDescriptor> _descriptors = [];
    private CancellationTokenSource? _windowOperationCancellation;
    private int _windowStart;
    private int _windowEnd;
    private int _descriptorRevision;
    private int _windowOperationRevision;
    private bool _isDisposed;
    private int _isRepartitionScheduled;

    /// <summary>
    /// Creates the presentation projection for one real chat context.
    /// </summary>
    /// <param name="context">The chat context whose selected branch is presented.</param>
    public ChatPresentation(ChatContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;

        // Keep the chat list physically flat for VariableHeightVirtualizingStackPanel. The
        // segmented list observes each turn independently and translates local insertions to the
        // correct global index without replacing rows owned by other turns.
        _visibleRows = new DynamicSegmentedList<IChatPresentationSegment, ChatPresentationRow>(_segments, segment => segment.Rows, int.MaxValue);
        Rows = _visibleRows.Items;

        _disposables.Add(context.ConnectDisplayItems().Subscribe(_ => RequestRepartition()));

        Repartition(publishSynchronously: true);
    }

    /// <summary>
    /// Prepares the bounded tail window before a loaded context becomes visible.
    /// </summary>
    public Task<bool> PrepareInitialWindowAsync(CancellationToken cancellationToken = default)
    {
        return ChangeWindowAsync(_windowStart, _windowEnd, supersedeCurrentOperation: true, cancellationToken);
    }

    /// <summary>
    /// Preheats and prepends the preceding complete turn batch.
    /// </summary>
    public Task<bool> LoadEarlierAsync(CancellationToken cancellationToken = default)
    {
        if (!HasEarlierTurns || _windowOperationCancellation is not null) return Task.FromResult(false);

        var start = Math.Max(0, _windowStart - TurnBatchSize);
        return ChangeWindowAsync(start, _windowEnd, supersedeCurrentOperation: false, cancellationToken);
    }

    /// <summary>
    /// Preheats and appends the following complete turn batch.
    /// </summary>
    public Task<bool> LoadLaterAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLaterTurns || _windowOperationCancellation is not null) return Task.FromResult(false);

        var end = Math.Min(_descriptors.Count, _windowEnd + TurnBatchSize);
        return ChangeWindowAsync(_windowStart, end, supersedeCurrentOperation: false, cancellationToken);
    }

    /// <summary>
    /// Replaces the current navigation window with a bounded, preheated tail window.
    /// </summary>
    public Task<bool> ShowLatestAsync(CancellationToken cancellationToken = default)
    {
        var start = Math.Max(0, _descriptors.Count - TurnBatchSize);
        if (_windowOperationCancellation is null && _windowStart == start && _windowEnd == _descriptors.Count)
            return Task.FromResult(true);

        return ChangeWindowAsync(start, _descriptors.Count, supersedeCurrentOperation: true, cancellationToken);
    }

    /// <summary>
    /// Shrinks the materialized window around the complete turns intersecting the viewport. Both
    /// rows must already belong to the committed window, so compaction never parses Markdown or
    /// materializes new turns.
    /// </summary>
    public bool CompactAround(ChatPresentationRow firstVisibleRow, ChatPresentationRow lastVisibleRow)
    {
        if (_isDisposed) return false;

        var firstVisibleIndex = FindTurnIndex(firstVisibleRow);
        var lastVisibleIndex = FindTurnIndex(lastVisibleRow);
        if (firstVisibleIndex < 0 || lastVisibleIndex < 0)
            return false;

        var visibleStart = Math.Min(firstVisibleIndex, lastVisibleIndex);
        var visibleEnd = Math.Max(firstVisibleIndex, lastVisibleIndex) + 1;
        var (start, end) = FitWindowAroundRange(visibleStart, visibleEnd, _windowStart, _windowEnd);
        if (!IsMaterializedRange(start, end))
            return false;

        // An earlier edge load may still be preparing a larger range. Once this synchronous
        // compaction commits, that obsolete operation must not expand the window again.
        _windowOperationCancellation?.Cancel();
        if (_windowStart == start && _windowEnd == end)
            return false;

        _windowStart = start;
        _windowEnd = end;
        SynchronizeMaterializedWindow();
        NotifyWindowStateChanged();
        return true;
    }

    /// <summary>
    /// Materializes a bounded window around a model-backed search target and resolves its row.
    /// </summary>
    public async Task<ChatPresentationRow?> RevealAsync(
        ChatMessageNode node,
        AssistantChatMessageSpan? span,
        CancellationToken cancellationToken = default)
    {
        var targetIndex = FindTurnIndex(node);
        if (targetIndex < 0) return null;

        if (_windowOperationCancellation is null &&
            targetIndex >= _windowStart &&
            targetIndex < _windowEnd &&
            ResolveTargetRow(node, span) is { } existing)
        {
            return existing;
        }

        var (start, end) = FitWindowAroundRange(targetIndex, targetIndex + 1, 0, _descriptors.Count);

        if (!await ChangeWindowAsync(start, end, supersedeCurrentOperation: true, cancellationToken))
            return null;

        return await Dispatcher.UIThread.InvokeAsync(() => ResolveTargetRow(node, span));
    }

    private async Task<bool> ChangeWindowAsync(
        int start,
        int end,
        bool supersedeCurrentOperation,
        CancellationToken cancellationToken)
    {
        if (_isDisposed) return false;

        if (_windowOperationCancellation is not null)
        {
            if (!supersedeCurrentOperation) return false;
            await _windowOperationCancellation.CancelAsync();
        }

        start = Math.Clamp(start, 0, _descriptors.Count);
        end = Math.Clamp(end, start, _descriptors.Count);

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _windowOperationCancellation = operationCancellation;
        var operation = ++_windowOperationRevision;
        var descriptorRevision = _descriptorRevision;
        var preparedTurns = new Dictionary<object, ChatTurnPresentation>(ReferenceEqualityComparer.Instance);
        var targetTurns = new List<ChatTurnPresentation>(end - start);

        for (var index = start; index < end; index++)
        {
            var descriptor = _descriptors[index];
            if (!_turns.TryGetValue(descriptor.Key, out var turn) || !turn.MatchesSources(descriptor.Nodes))
            {
                turn = new ChatTurnPresentation();
                turn.UpdateSources(descriptor.Nodes, GetBusyActivities(descriptor));
                preparedTurns.Add(descriptor.Key, turn);
            }

            targetTurns.Add(turn);
        }

        var requests = CaptureMarkdownPreparationRequests(targetTurns);
        try
        {
            var updates = await ParseMarkdownAsync(requests, operationCancellation.Token).ConfigureAwait(false);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed ||
                    operationCancellation.IsCancellationRequested ||
                    operation != _windowOperationRevision ||
                    descriptorRevision != _descriptorRevision)
                {
                    return false;
                }

                for (var i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    var update = updates[i];
                    if (request.Builder.Version != update.Version)
                        return false;
                }

                for (var i = 0; i < requests.Count; i++)
                {
                    var request = requests[i];
                    var update = updates[i];
                    if (request.Row.CachedDocumentUpdate?.Version != update.Version)
                        request.Row.CachedDocumentUpdate = update;
                }

                var replacedTurns = new List<ChatTurnPresentation>();
                foreach (var pair in preparedTurns)
                {
                    if (_turns.TryGetValue(pair.Key, out var replacedTurn))
                        replacedTurns.Add(replacedTurn);
                    _turns[pair.Key] = pair.Value;
                }
                preparedTurns.Clear();

                _windowStart = start;
                _windowEnd = end;
                SynchronizeMaterializedWindow();
                foreach (var replacedTurn in replacedTurns) replacedTurn.Dispose();
                NotifyWindowStateChanged();
                return true;
            });
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                foreach (var turn in preparedTurns.Values) turn.Dispose();
                preparedTurns.Clear();

                if (ReferenceEquals(_windowOperationCancellation, operationCancellation))
                    _windowOperationCancellation = null;

                operationCancellation.Dispose();
            });
        }
    }

    private static List<MarkdownPreparationRequest> CaptureMarkdownPreparationRequests(IReadOnlyList<ChatTurnPresentation> turns)
    {
        var requests = new List<MarkdownPreparationRequest>();
        foreach (var row in turns.AsValueEnumerable().SelectMany(static turn => turn.OutputRows).OfType<AssistantTextOutputPresentationRow>())
        {
            if (row.CanReceiveUpdates) continue;

            var builder = row.TextSpan.ContentMarkdownBuilder;
            var snapshot = builder.CaptureSnapshot();
            if (row.CachedDocumentUpdate?.Version == snapshot.Version) continue;

            requests.Add(new MarkdownPreparationRequest(row, builder, snapshot));
        }

        return requests;
    }

    private static Task<IReadOnlyList<MarkdownDocumentUpdate>> ParseMarkdownAsync(
        IReadOnlyList<MarkdownPreparationRequest> requests,
        CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
            return Task.FromResult<IReadOnlyList<MarkdownDocumentUpdate>>([]);

        var pipeline = MarkdownUpdateProducer.DefaultPipeline;
        var snapshots = requests.Select(static request => request.Snapshot).ToArray();
        return Task.Run<IReadOnlyList<MarkdownDocumentUpdate>>(
            () =>
            {
                var result = new List<MarkdownDocumentUpdate>(snapshots.Length);
                foreach (var snapshot in snapshots)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var document = Markdown.Parse(snapshot.Text, pipeline);
                    result.Add(new MarkdownDocumentUpdate.Full(document, snapshot.Version));
                }

                return result;
            },
            cancellationToken);
    }

    private void RequestRepartition()
    {
        // ChatService mutates the selected branch from its worker task. Coalesce a burst of node
        // changes into one dispatcher pass so the DynamicData projection is never edited from that
        // worker and the UI does not process obsolete intermediate branch shapes. PostOnDemand
        // executes inline when this notification already originates from the UI thread.
        if (Interlocked.Exchange(ref _isRepartitionScheduled, 1) != 0) return;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            Interlocked.Exchange(ref _isRepartitionScheduled, 0);
            Repartition();
        });
    }

    /// <summary>
    /// Adds a runtime-only activity to the current assistant turn. The returned scope applies its
    /// explicit completion policy without changing the persisted message graph.
    /// </summary>
    public IDisposable SetBusyActivity(LucideIconKind icon, IDynamicLocaleKey headerKey, bool removeAfterCompletion)
    {
        var scope = new BusyActivityScope(
            this,
            new BusyActivityItemPresentationRow(icon, headerKey, DateTimeOffset.UtcNow),
            removeAfterCompletion);
        DispatchBusyActivityUpdate(scope);
        return scope;
    }

    private void DispatchBusyActivityUpdate(BusyActivityScope scope)
    {
        Dispatcher.UIThread.PostOnDemand(() => SynchronizeBusyActivity(scope));
    }

    private void SynchronizeBusyActivity(BusyActivityScope scope)
    {
        if (_isDisposed) return;

        if (!scope.IsAttached)
        {
            scope.IsAttached = true;
            var assistantNode = _context.Items.Count > 0 ? _context.Items[^1] : null;

            // SetBusyActivityAsync is normally entered after ChatService has appended the busy assistant
            // node. If a future caller violates that ordering, silently omit the visual activity
            // instead of manufacturing a message node or weakening the persistence boundary.
            if (assistantNode?.Message is not AssistantChatMessage assistant) return;

            scope.Row.AssistantNode = assistantNode;
            scope.Row.AnchorSpan = assistant.Spans.LastOrDefault();
            _busyActivities.Add(scope.Row);
        }

        if (scope.FinishedAt is { } finishedAt)
        {
            if (scope.RemoveAfterCompletion) _busyActivities.Remove(scope.Row);
            else scope.Row.Complete(finishedAt);
        }

        Repartition();
    }

    private void Repartition(bool publishSynchronously = false)
    {
        if (_isDisposed) return;

        // Preserve the current logical window by stable turn identity. A normal tail append extends
        // a latest window; a branch replacement that invalidates the materialized sequence falls
        // back to a bounded tail rather than attempting to splice unrelated rows together.
        var previousKeys = _descriptors
            .Skip(_windowStart)
            .Take(_windowEnd - _windowStart)
            .Select(static descriptor => descriptor.Key)
            .ToArray();
        var wasAtLatest = _windowEnd == _descriptors.Count;

        _descriptors = BuildTurnDescriptors(
            _context.Items
                .AsValueEnumerable()
                .Where(node => !node.Message.IsHidden)
                .ToArray());
        _descriptorRevision++;
        _windowOperationCancellation?.Cancel();

        int start;
        int end;
        if (_descriptors.Count == 0)
        {
            start = 0;
            end = 0;
        }
        else if (previousKeys.Length == 0 || !TryResolveContiguousRange(previousKeys, out start, out end))
        {
            start = Math.Max(0, _descriptors.Count - TurnBatchSize);
            end = _descriptors.Count;
        }
        else
        {
            end = wasAtLatest ? _descriptors.Count : end;
        }

        if (publishSynchronously || CanSynchronizeWithoutPreheating(start, end))
        {
            _windowStart = start;
            _windowEnd = end;
            SynchronizeMaterializedWindow();
            NotifyWindowStateChanged();
            return;
        }

        ApplyRepartitionAsync(start, end).Detach();
    }

    private bool CanSynchronizeWithoutPreheating(int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            var descriptor = _descriptors[index];
            if (_turns.TryGetValue(descriptor.Key, out var turn))
            {
                if (!turn.MatchesSources(descriptor.Nodes)) return false;
                continue;
            }

            foreach (var node in descriptor.Nodes)
            {
                if (node.Message is not AssistantChatMessage assistant) continue;
                if (assistant.Spans.AsValueEnumerable().OfType<AssistantChatMessageTextSpan>()
                    .Any(span => !assistant.IsBusy || span.FinishedAt is not null))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task ApplyRepartitionAsync(int start, int end)
    {
        try
        {
            await ChangeWindowAsync(start, end, supersedeCurrentOperation: true, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to apply a chat presentation branch change.");
        }
    }

    private void SynchronizeMaterializedWindow()
    {
        var desired = new List<IChatPresentationSegment>(_windowEnd - _windowStart);
        var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var index = _windowStart; index < _windowEnd; index++)
        {
            var descriptor = _descriptors[index];
            retained.Add(descriptor.Key);
            if (!_turns.TryGetValue(descriptor.Key, out var turn))
            {
                turn = new ChatTurnPresentation();
                _turns.Add(descriptor.Key, turn);
            }

            turn.UpdateSources(descriptor.Nodes, GetBusyActivities(descriptor));
            desired.Add(turn);
        }

        ReconcileByReference(_segments, desired);

        foreach (var removed in _turns.AsValueEnumerable().Where(pair => !retained.Contains(pair.Key)).ToArray())
        {
            _turns.Remove(removed.Key);
            removed.Value.Dispose();
        }
    }

    private IReadOnlyList<BusyActivityItemPresentationRow> GetBusyActivities(TurnDescriptor descriptor) =>
        _busyActivities
            .AsValueEnumerable()
            .Where(activity => descriptor.Nodes.AsValueEnumerable().Any(node => ReferenceEquals(node, activity.AssistantNode)))
            .ToArray();

    private bool TryResolveContiguousRange(IReadOnlyList<object> keys, out int start, out int end)
    {
        start = FindDescriptorIndex(keys[0]);
        if (start < 0)
        {
            end = -1;
            return false;
        }

        for (var offset = 1; offset < keys.Count; offset++)
        {
            var index = start + offset;
            if (index >= _descriptors.Count || !ReferenceEquals(_descriptors[index].Key, keys[offset]))
            {
                end = -1;
                return false;
            }
        }

        end = start + keys.Count;
        return true;
    }

    private int FindDescriptorIndex(object key)
    {
        for (var index = 0; index < _descriptors.Count; index++)
        {
            if (ReferenceEquals(_descriptors[index].Key, key)) return index;
        }

        return -1;
    }

    private int FindTurnIndex(ChatMessageNode node)
    {
        for (var index = 0; index < _descriptors.Count; index++)
        {
            if (_descriptors[index].Nodes.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, node)))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// Resolves a materialized row to its preceding user turn for viewport navigation.
    /// </summary>
    public ChatMessageNode? GetUserTurnNode(ChatPresentationRow row)
    {
        for (var i = FindTurnIndex(row); i >= 0; i--)
        {
            if (_descriptors[i].Nodes.FirstOrDefault() is { Message.Role.Label: "user" } node) return node;
        }
        return null;
    }

    private int FindTurnIndex(ChatPresentationRow row)
    {
        for (var index = _windowStart; index < _windowEnd; index++)
        {
            var descriptor = _descriptors[index];
            if (!_turns.TryGetValue(descriptor.Key, out var turn) || !turn.MatchesSources(descriptor.Nodes))
                continue;

            if (turn.Rows.Items.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, row)))
                return index;
        }

        return -1;
    }

    private bool IsMaterializedRange(int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            var descriptor = _descriptors[index];
            if (!_turns.TryGetValue(descriptor.Key, out var turn) || !turn.MatchesSources(descriptor.Nodes))
                return false;
        }

        return true;
    }

    private static (int Start, int End) FitWindowAroundRange(int targetStart, int targetEnd, int boundsStart, int boundsEnd)
    {
        var targetLength = targetEnd - targetStart;
        var length = Math.Min(boundsEnd - boundsStart, Math.Max(TurnBatchSize, targetLength));
        var leadingPadding = (length - targetLength + 1) / 2;
        var minimumStart = Math.Max(boundsStart, targetEnd - length);
        var maximumStart = Math.Min(targetStart, boundsEnd - length);
        var start = Math.Clamp(targetStart - leadingPadding, minimumStart, maximumStart);
        return (start, start + length);
    }

    private ChatPresentationRow? ResolveTargetRow(ChatMessageNode node, AssistantChatMessageSpan? span)
    {
        var index = FindTurnIndex(node);
        if (index < _windowStart || index >= _windowEnd) return null;

        var descriptor = _descriptors[index];
        return _turns.TryGetValue(descriptor.Key, out var turn) ? turn.ResolveTargetRow(node, span) : null;
    }

    private void NotifyWindowStateChanged()
    {
        OnPropertyChanged(nameof(HasEarlierTurns));
        OnPropertyChanged(nameof(HasLaterTurns));
        OnPropertyChanged(nameof(IsAtLatest));
    }

    private static List<TurnDescriptor> BuildTurnDescriptors(ChatMessageNode[] nodes)
    {
        var result = new List<TurnDescriptor>();
        List<ChatMessageNode>? current = null;
        object? currentKey = null;

        void Flush()
        {
            if (current is not { Count: > 0 } || currentKey is null) return;
            result.Add(new TurnDescriptor(currentKey, [.. current]));
            current = null;
            currentKey = null;
        }

        foreach (var node in nodes.AsValueEnumerable())
        {
            if (node.Message.Role.Label == "user")
            {
                Flush();
                current = [node];
                currentKey = node;
                continue;
            }

            if (current is not null &&
                (node.Message is AssistantChatMessage || node.Message is ActionChatMessage && !current.Any(x => x.Message is AssistantChatMessage)))
            {
                current.Add(node);
                continue;
            }

            if (node.Message is AssistantChatMessage)
            {
                current ??= [];
                currentKey ??= node;
                current.Add(node);
                continue;
            }

            Flush();
            result.Add(new TurnDescriptor(node, [node]));
        }

        Flush();
        return result;
    }

    private static void ReconcileByReference<T>(SourceList<T> source, IReadOnlyList<T> desired) where T : class
    {
        source.Edit(list =>
        {
            var prefix = 0;
            while (prefix < list.Count && prefix < desired.Count && ReferenceEquals(list[prefix], desired[prefix]))
            {
                prefix++;
            }

            var suffix = 0;
            while (suffix < list.Count - prefix && suffix < desired.Count - prefix &&
                   ReferenceEquals(list[list.Count - 1 - suffix], desired[desired.Count - 1 - suffix]))
            {
                suffix++;
            }

            var removeCount = list.Count - prefix - suffix;
            if (removeCount > 0) list.RemoveRange(prefix, removeCount);
            var insertCount = desired.Count - prefix - suffix;
            if (insertCount > 0) list.InsertRange(desired.Skip(prefix).Take(insertCount), prefix);
        });
    }

    /// <summary>
    /// Releases branch subscriptions and turn-local sources. Only the owning ChatContext calls
    /// this method, ensuring a view cannot accidentally invalidate presentation shared by another
    /// view of the same context.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _windowOperationCancellation?.Cancel();
        _windowOperationCancellation?.Dispose();
        _windowOperationCancellation = null;
        _disposables.Dispose();
        _segments.Clear();
        foreach (var turn in _turns.Values) turn.Dispose();
        _turns.Clear();
        _descriptors.Clear();
        _busyActivities.Clear();
        _visibleRows.Dispose();
        _segments.Dispose();
    }

    private sealed record TurnDescriptor(object Key, IReadOnlyList<ChatMessageNode> Nodes);

    private interface IChatPresentationSegment : IDisposable
    {
        IObservableList<ChatPresentationRow> Rows { get; }
    }

    private readonly record struct MarkdownPreparationRequest(
        AssistantTextOutputPresentationRow Row,
        ObservableStringBuilder Builder,
        ObservableStringBuilderSnapshot Snapshot
    );

    /// <summary>
    /// Thread-safe lifetime token returned to background chat operations. Only the completion
    /// timestamp crosses threads; all row attachment and SourceList work is dispatched through the
    /// owning presentation on Avalonia's UI thread.
    /// </summary>
    private sealed class BusyActivityScope(ChatPresentation owner, BusyActivityItemPresentationRow row, bool removeAfterCompletion) : IDisposable
    {
        public BusyActivityItemPresentationRow Row { get; } = row;
        public bool RemoveAfterCompletion { get; } = removeAfterCompletion;
        public bool IsAttached { get; set; }

        public DateTimeOffset? FinishedAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _finishedAtUtcTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        private long _finishedAtUtcTicks;

        public void Dispose()
        {
            var ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            if (Interlocked.CompareExchange(ref _finishedAtUtcTicks, ticks, 0) != 0) return;
            owner.DispatchBusyActivityUpdate(this);
        }
    }

    /// <summary>
    /// Owns all presentation state and source subscriptions for one user-delimited turn. Rebuilding
    /// its inexpensive entry sequence never rebuilds row objects: role-specific registries retain
    /// them and the visible SourceList receives only a reference-based structural difference.
    /// </summary>
    private sealed class ChatTurnPresentation : IChatPresentationSegment
    {
        private static readonly IDynamicLocaleKey ReasoningHeader = new DynamicLocaleKey(LocaleKey.ChatMessageControl_Assistant_Reasoning);
        private static readonly IDynamicLocaleKey GenericHeader = new DynamicLocaleKey(LocaleKey.ChatPresentation_GenericActivity);

        /// <summary>
        /// Completed activity collections at or below this size are shown as direct activity rows.
        /// Larger collections keep their aggregate container to avoid overwhelming the conversation.
        /// </summary>
        private const int InlineActivityLimit = 2;

        private readonly SourceList<ChatPresentationRow> _visibleRows = new();
        private readonly Dictionary<ChatMessageNode, ChatMessagePresentationRow> _messageRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, ActivityItemPresentationRow> _activityRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, ActivityGroupPresentationRow> _groupRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<AssistantChatMessageSpan, AssistantOutputPresentationRow> _outputRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, PendingAssistantPresentationRow> _pendingRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, TurnFooterPresentationRow> _footerRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, NoResponsePresentationRow> _noResponseRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(ChatMessageNode Node, bool Terminal), AssistantErrorPresentationRow> _errorRows = new();
        private readonly HashSet<ActivityGroupPresentationRow> _groupsAwaitingFinalPlacement = new(ReferenceEqualityComparer.Instance);

        private CompositeDisposable _subscriptions = new();
        private IReadOnlyList<ChatMessageNode> _nodes = [];
        private IReadOnlyList<BusyActivityItemPresentationRow> _busyActivities = [];
        private bool _isDisposed;

        // All fields below are UI-thread-only. Model notifications enter through the two handlers
        // below, which use PostOnDemand before changing these flags. Keeping the state local to the
        // dispatcher makes the refresh protocol easier to reason about and avoids a lock around a
        // state machine that can never be legitimately advanced by two threads at once.
        private bool _refreshPosted;
        private bool _refreshRequested;
        private bool _rewireRequested;
        private bool _isApplyingRefresh;

        public IObservableList<ChatPresentationRow> Rows => _visibleRows;
        internal IEnumerable<AssistantOutputPresentationRow> OutputRows => _outputRows.Values;
        private ProcessSummaryPresentationRow SummaryRow => field ??= new ProcessSummaryPresentationRow(RowsChanged);

        internal bool MatchesSources(IReadOnlyList<ChatMessageNode> nodes) => ReferencesEqual(_nodes, nodes);

        /// <summary>
        /// Replaces the turn's persisted node view and runtime-only activity view by reference.
        /// Persisted membership changes require subscription rewiring; a transient activity change
        /// only requires a cheap structural rebuild because those rows are completed or removed
        /// explicitly by their owning scope.
        /// </summary>
        public void UpdateSources(IReadOnlyList<ChatMessageNode> nodes, IReadOnlyList<BusyActivityItemPresentationRow> busyActivities)
        {
            // Called by ChatPresentation.Repartition on the dispatcher. Keeping this method
            // synchronous is intentional: it lets a branch snapshot and its row reconciliation
            // complete as one UI transaction, while streamed source notifications use the refresh
            // coalescer below instead of entering this method from a worker.
            var nodesChanged = !ReferencesEqual(_nodes, nodes);
            var busyActivitiesChanged = !ReferencesEqual(_busyActivities, busyActivities);
            if (!nodesChanged && !busyActivitiesChanged)
            {
                // The outer presentation also calls Repartition when an existing transient row is
                // completed. Its reference is intentionally stable, so refresh structural state
                // even though neither source collection changed membership.
                RebuildVisibleRows();
                return;
            }

            if (busyActivitiesChanged)
            {
                // A removable runtime activity can be the identity key of its former Group. Once
                // that activity leaves the source list, the Group can no longer be reached by a
                // later entry sequence and should not remain cached for the rest of the chat.
                foreach (var removed in _busyActivities.AsValueEnumerable().Where(activity =>
                             !busyActivities.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, activity))))
                {
                    _groupRows.Remove(removed);
                }
            }

            _nodes = nodes;
            _busyActivities = busyActivities;
            if (nodesChanged) Rewire();
            else RebuildVisibleRows();
        }

        private static bool ReferencesEqual<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) where T : class =>
            first.Count == second.Count && first.Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second));

        private void Rewire()
        {
            if (_isDisposed) return;

            // Rewire can be called synchronously by the outer turn projection. Mark the whole
            // operation as a projection pass so a source callback raised while subscriptions are
            // being replaced is queued instead of recursively entering another pass.
            var wasApplyingRefresh = _isApplyingRefresh;
            _isApplyingRefresh = true;
            try
            {
                // Collection membership changes are much less frequent than streamed property changes.
                // Rebuilding this turn-local subscription set keeps removal/disposal exact without
                // maintaining a second nested ownership graph; it never touches another turn's rows.
                _subscriptions.Dispose();
                _subscriptions = new CompositeDisposable();
                var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var activeActivitySources = new HashSet<object>(ReferenceEqualityComparer.Instance);
                foreach (var node in _nodes.AsValueEnumerable())
                {
                    SubscribeProperties(node.Message, visited);
                    if (node.Message is not AssistantChatMessage assistant) continue;
                    SubscribeCollection(assistant.Spans, visited);
                    // A deserialized branch should contain only real span instances, but filtering
                    // here keeps a malformed/null entry from taking down the entire UI refresh pass.
                    foreach (var span in assistant.Spans.AsValueEnumerable().OfType<AssistantChatMessageSpan>())
                    {
                        SubscribeProperties(span, visited);
                        if (span is AssistantChatMessageFunctionCallSpan functions)
                        {
                            SubscribeCollection(functions.FunctionCalls, visited);
                            foreach (var function in functions.FunctionCalls.AsValueEnumerable().OfType<FunctionCallChatMessage>())
                            {
                                activeActivitySources.Add(function);
                                SubscribeProperties(function, visited);
                                SubscribeCollection(function.DisplayBlocks, visited);
                                foreach (var block in function.DisplayBlocks.AsValueEnumerable().OfType<ChatPluginDisplayBlock>())
                                    SubscribeBlock(block, visited);
                            }
                        }
                        else if (span is AssistantChatMessageReasoningSpan reasoning)
                        {
                            activeActivitySources.Add(reasoning);
                        }
                    }
                }

                // Rows are stable while their source remains on the selected branch. Once a branch
                // replacement removes a span or function call, retaining its row in this cache would
                // make every later streamed text update refresh a disposed, no-longer-visible source.
                foreach (var source in _activityRows.Keys.AsValueEnumerable().Where(source => !activeActivitySources.Contains(source)).ToArray())
                {
                    _activityRows.Remove(source);
                }

                RebuildVisibleRows();
            }
            finally
            {
                _isApplyingRefresh = wasApplyingRefresh;
            }
        }

        private void SubscribeBlock(ChatPluginDisplayBlock? block, HashSet<object> visited)
        {
            if (block is null) return;
            if (!visited.Add(block)) return;

            block.PropertyChanged += HandlePropertyChanged;
            _subscriptions.Add(Disposable.Create(() => block.PropertyChanged -= HandlePropertyChanged));
            if (block is not ChatPluginContainerDisplayBlock container) return;

            SubscribeCollection(container.Children, visited);
            foreach (var child in container.Children) SubscribeBlock(child, visited);
        }

        private void SubscribeCollection(INotifyCollectionChanged? source, HashSet<object> visited)
        {
            if (source is null) return;
            if (!visited.Add(source)) return;

            source.CollectionChanged += HandleCollectionChanged;
            _subscriptions.Add(Disposable.Create(() => source.CollectionChanged -= HandleCollectionChanged));
        }

        private void SubscribeProperties(ObservableObject? source, HashSet<object> visited)
        {
            if (source is null) return;
            if (!visited.Add(source)) return;

            source.PropertyChanged += HandlePropertyChanged;
            _subscriptions.Add(Disposable.Create(() => source.PropertyChanged -= HandlePropertyChanged));
        }

        private void HandleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            RequestRefresh(rewire: true);
        }

        private void HandlePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // A streamed model property (for example, a markdown builder or IsBusy) can be raised
            // by the worker that owns the chat request. Do not even touch the UI-only refresh flags
            // until the notification has crossed the dispatcher boundary.
            Dispatcher.UIThread.PostOnDemand(() => RequestRefresh());
        }

        private void RequestRefresh(bool rewire = false)
        {
            _refreshRequested = true;
            _rewireRequested |= rewire;
            if (_refreshPosted) return;

            _refreshPosted = true;
            // Preserve synchronous UI updates when the projection is idle. A callback raised while
            // Rewire or RefreshFromSources is in progress is deferred to the already-coalesced
            // dispatcher pass and therefore cannot recursively enter either operation.
            if (!_isApplyingRefresh)
            {
                DrainRefresh();
            }
            else Dispatcher.UIThread.Post(DrainRefresh);
        }

        private void DrainRefresh()
        {
            var shouldRepost = false;
            try
            {
                while (true)
                {
                    var refreshRequested = _refreshRequested;
                    var rewireRequested = _rewireRequested;
                    _refreshRequested = false;
                    _rewireRequested = false;

                    if (!refreshRequested && !rewireRequested)
                    {
                        break;
                    }

                    if (_isDisposed) break;

                    // Rewire already performs one structural rebuild. If the same burst also carried
                    // a property notification, retain that part of the request: rebuilding
                    // subscriptions alone does not raise the row-level PropertyChanged events that
                    // bindings use for source-backed previews and counters.
                    if (rewireRequested)
                    {
                        Rewire();
                        if (refreshRequested) RefreshFromSources();
                    }
                    else if (refreshRequested)
                    {
                        RefreshFromSources();
                    }
                }
            }
            finally
            {
                if (_isDisposed)
                {
                    _refreshRequested = false;
                    _rewireRequested = false;
                    _refreshPosted = false;
                }
                else if (_refreshRequested || _rewireRequested)
                {
                    // A notification can arrive while the current pass is running. The current
                    // callback finishes before another dispatcher pass starts; no lock is needed
                    // because both the callback and this cleanup execute on the UI thread.
                    _refreshPosted = false;
                    shouldRepost = true;
                }
                else
                {
                    _refreshPosted = false;
                }
            }

            if (shouldRepost) RequestRefresh();
        }

        private void RefreshFromSources()
        {
            if (_isDisposed) return;

            var wasApplyingRefresh = _isApplyingRefresh;
            _isApplyingRefresh = true;
            try
            {
                // Rows read most values directly from source objects. Refresh their bindings first, then
                // reconcile structural state in case completion promoted output or changed a group.
                foreach (var activity in _activityRows.Values) activity.Refresh();
                RebuildVisibleRows();
            }
            finally
            {
                _isApplyingRefresh = wasApplyingRefresh;
            }
        }

        private void RowsChanged() => RebuildVisibleRows();

        private void RebuildVisibleRows()
        {
            if (_isDisposed) return;

            var desired = _nodes.Where(node => node.Message is not AssistantChatMessage).Select(GetMessageRow).Cast<ChatPresentationRow>().ToList();
            var assistants = _nodes.AsValueEnumerable().Where(node => node.Message is AssistantChatMessage).ToArray();
            var latestNode = assistants.LastOrDefault();
            var latest = latestNode?.Message as AssistantChatMessage;
            var isRunning = latest?.IsBusy is true;
            var entries = BuildEntries(
                assistants,
                includeLatestError: isRunning,
                keepTrailingActivityOpen: isRunning);

            if (isRunning)
            {
                AppendEntries(entries, desired, false);
                if (latestNode is not null && latest is { Count: 0 }) desired.Add(GetPendingRow(latestNode));
                if (latestNode is not null) desired.Add(GetFooterRow(latestNode));
                ReconcileByReference(_visibleRows, desired);
                return;
            }

            if (latestNode is null || latest is null)
            {
                AppendEntries(entries, desired, false);
                ReconcileByReference(_visibleRows, desired);
                return;
            }

            if (latest.ErrorMessageKey is not null)
            {
                AppendCompletedProcess(
                    BuildEntries(
                        assistants,
                        includeLatestError: false,
                        keepTrailingActivityOpen: false),
                    desired);
                desired.Add(GetErrorRow(latestNode, true));
                desired.Add(GetFooterRow(latestNode));
                ReconcileByReference(_visibleRows, desired);
                return;
            }

            var finalStart = FindFinalOutputStart(entries, latestNode);
            var process = finalStart < 0 ? entries : [.. entries.Take(finalStart)];
            var final = finalStart < 0 ? [] : entries.Skip(finalStart).ToArray();
            AppendCompletedProcess(process, desired);
            AppendEntries(final, desired, true);

            if (final.Length == 0) desired.Add(GetNoResponseRow(latestNode));
            desired.Add(GetFooterRow(latestNode));
            ReconcileByReference(_visibleRows, desired);
        }

        private List<Entry> BuildEntries(ChatMessageNode[] assistants, bool includeLatestError, bool keepTrailingActivityOpen)
        {
            var result = new List<Entry>();
            var pending = new List<ActivityItemPresentationRow>();

            void Flush(bool isAwaitingContinuation)
            {
                if (pending.Count == 0) return;
                var group = GetGroupRow(pending[0].Source);
                if (group.UpdateItems(pending, isAwaitingContinuation)) ScheduleFinalPlacement(group);
                result.Add(new GroupEntry(group));
                pending.Clear();
            }

            void AppendBusyActivities(ChatMessageNode node, AssistantChatMessageSpan? anchorSpan)
            {
                foreach (var activity in _busyActivities.AsValueEnumerable())
                {
                    if (ReferenceEquals(activity.AssistantNode, node) &&
                        ReferenceEquals(activity.AnchorSpan, anchorSpan))
                        pending.Add(activity);
                }
            }

            for (var assistantIndex = 0; assistantIndex < assistants.Length; assistantIndex++)
            {
                if (assistantIndex > 0) Flush(isAwaitingContinuation: false);
                var node = assistants[assistantIndex];
                var assistant = (AssistantChatMessage)node.Message;
                AppendBusyActivities(node, null);
                foreach (var span in assistant.Spans.AsValueEnumerable().OfType<AssistantChatMessageSpan>())
                {
                    switch (span)
                    {
                        case AssistantChatMessageReasoningSpan reasoning:
                            pending.Add(GetReasoningRow(assistant, reasoning));
                            break;
                        case AssistantChatMessageFunctionCallSpan functionSpan:
                            pending.AddRange(
                                functionSpan.FunctionCalls.AsValueEnumerable()
                                    .OfType<FunctionCallChatMessage>()
                                    .Select(GetFunctionRow)
                                    .ToArray());
                            break;
                        case AssistantChatMessageTextSpan:
                        case AssistantChatMessageImageSpan:
                            Flush(isAwaitingContinuation: false);
                            result.Add(new OutputEntry(GetOutputRow(node, span)));
                            break;
                    }

                    // A temporary activity is anchored to the latest span observed when its scope
                    // begins. Appending it after that span preserves the real chronology without
                    // writing a placeholder into AssistantChatMessage.Spans.
                    AppendBusyActivities(node, span);
                }

                // Only a process segment at the absolute end of the latest assistant invocation is
                // kept open. Earlier Groups have already been terminated by formal output or an
                // assistant boundary and must never inherit the whole-turn busy state.
                Flush(keepTrailingActivityOpen && assistantIndex == assistants.Length - 1);
                if (assistant.ErrorMessageKey is not null && (assistantIndex < assistants.Length - 1 || includeLatestError))
                {
                    result.Add(new ErrorEntry(GetErrorRow(node, false)));
                }
            }

            return result;
        }

        private void AppendCompletedProcess(IReadOnlyList<Entry> entries, List<ChatPresentationRow> desired)
        {
            if (entries.Count == 0) return;
            var items = entries.OfType<GroupEntry>().SelectMany(entry => entry.Group.Items).ToArray();
            SummaryRow.UpdateStatistics(ChatActivityStatistics.Calculate(items));

            // Do not replace a just-completed running card with the final process summary while its
            // glow is still fading. The delayed callback rebuilds this turn after the visual morph,
            // at which point the normal direct-row or summary rule is applied.
            if (entries.OfType<GroupEntry>().Any(entry => _groupsAwaitingFinalPlacement.Contains(entry.Group)) ||
                items.Length is > 0 and <= InlineActivityLimit)
            {
                AppendEntries(entries, desired, false);
                return;
            }

            desired.Add(SummaryRow);
            if (SummaryRow.IsExpanded) AppendEntries(entries, desired, false);
        }

        private static int FindFinalOutputStart(IReadOnlyList<Entry> entries, ChatMessageNode latest)
        {
            var index = entries.Count;
            while (index > 0 && entries[index - 1] is OutputEntry output && ReferenceEquals(output.Row.AssistantNode, latest)) index--;
            return index == entries.Count ? -1 : index;
        }

        private void AppendEntries(IReadOnlyList<Entry> entries, List<ChatPresentationRow> desired, bool isFinal)
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case GroupEntry group:
                        // Every running segment remains a Group, even when it currently contains a
                        // single item, so the user receives the glow and live status treatment. A
                        // completed Group is held in that shape for one short transition window;
                        // afterwards up to two items are promoted directly and larger groups stay
                        // as one collapsed outer row.
                        if (!group.Group.IsRunning &&
                            !_groupsAwaitingFinalPlacement.Contains(group.Group) &&
                            group.Group.Items.Count <= InlineActivityLimit)
                        {
                            desired.AddRange(group.Group.Items);
                            break;
                        }

                        desired.Add(group.Group);
                        break;
                    case OutputEntry output:
                        output.Row.IsFinal = isFinal;
                        desired.Add(output.Row);
                        break;
                    case ErrorEntry error:
                        desired.Add(error.Row);
                        break;
                }
            }
        }

        private ChatMessagePresentationRow GetMessageRow(ChatMessageNode node) =>
            _messageRows.GetValueOrDefault(node) ?? (_messageRows[node] = new ChatMessagePresentationRow(node));

        private PendingAssistantPresentationRow GetPendingRow(ChatMessageNode node) =>
            _pendingRows.GetValueOrDefault(node) ?? (_pendingRows[node] = new PendingAssistantPresentationRow());

        private TurnFooterPresentationRow GetFooterRow(ChatMessageNode node) =>
            _footerRows.GetValueOrDefault(node) ?? (_footerRows[node] = new TurnFooterPresentationRow(node));

        private NoResponsePresentationRow GetNoResponseRow(ChatMessageNode node) =>
            _noResponseRows.GetValueOrDefault(node) ?? (_noResponseRows[node] = new NoResponsePresentationRow());

        private AssistantErrorPresentationRow GetErrorRow(ChatMessageNode node, bool terminal) =>
            _errorRows.GetValueOrDefault((node, terminal)) ?? (_errorRows[(node, terminal)] = new AssistantErrorPresentationRow(node, terminal));

        public ChatPresentationRow? ResolveTargetRow(ChatMessageNode node, AssistantChatMessageSpan? span)
        {
            if (span is null)
                return _messageRows.GetValueOrDefault(node);

            if (!_outputRows.TryGetValue(span, out var row)) return null;
            if (_visibleRows.Items.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, row))) return row;

            // A failed partial output can live inside a collapsed process summary. Search navigation
            // explicitly targets that output, so reveal the existing rows rather than manufacturing
            // a parallel presentation path for the same span.
            SummaryRow.IsExpanded = true;
            return _visibleRows.Items.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, row)) ? row : null;
        }

        private AssistantOutputPresentationRow GetOutputRow(ChatMessageNode node, AssistantChatMessageSpan span)
        {
            var row = _outputRows.GetValueOrDefault(span) ?? (_outputRows[span] = span switch
            {
                AssistantChatMessageTextSpan text => new AssistantTextOutputPresentationRow(node, text),
                AssistantChatMessageImageSpan image => new AssistantImageOutputPresentationRow(node, image),
                _ => throw new InvalidOperationException($"Unexpected span type {span.GetType().Name}")
            });

            if (row is AssistantTextOutputPresentationRow textRow)
            {
                var assistant = (AssistantChatMessage)node.Message;
                textRow.UpdateCanReceiveUpdates(assistant.IsBusy && span.FinishedAt is null);
            }

            return row;
        }

        private ActivityGroupPresentationRow GetGroupRow(object source) =>
            _groupRows.GetValueOrDefault(source) ?? (_groupRows[source] = new ActivityGroupPresentationRow());

        private ActivityItemPresentationRow GetReasoningRow(AssistantChatMessage assistant, AssistantChatMessageReasoningSpan reasoning) =>
            _activityRows.GetValueOrDefault(reasoning) ??
            (_activityRows[reasoning] = new ReasoningActivityItemPresentationRow(assistant, reasoning, ReasoningHeader));

        private ActivityItemPresentationRow GetFunctionRow(FunctionCallChatMessage function) =>
            _activityRows.GetValueOrDefault(function) ??
            (_activityRows[function] = new FunctionCallActivityItemPresentationRow(function, GenericHeader));

        private void ScheduleFinalPlacement(ActivityGroupPresentationRow group)
        {
            if (!_groupsAwaitingFinalPlacement.Add(group)) return;

            DispatcherTimer.RunOnce(
                () =>
                {
                    if (_isDisposed) return;
                    _groupsAwaitingFinalPlacement.Remove(group);

                    // A new activity can join the same segment during the transition window. In that
                    // case the Group remains expanded and its next real completion will schedule a new
                    // final-placement pass.
                    if (group.IsRunning) return;

                    group.SetExpandedFromPresentation(false);
                    RebuildVisibleRows();
                },
                TimeSpan.FromMilliseconds(400)); // Leaves a just-completed running Group in place long enough for the 320 ms glow transition
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _subscriptions.Dispose();
            _groupsAwaitingFinalPlacement.Clear();
            _visibleRows.Dispose();
        }

        private abstract record Entry;

        private sealed record GroupEntry(ActivityGroupPresentationRow Group) : Entry;

        private sealed record OutputEntry(AssistantOutputPresentationRow Row) : Entry;

        private sealed record ErrorEntry(AssistantErrorPresentationRow Row) : Entry;
    }
}

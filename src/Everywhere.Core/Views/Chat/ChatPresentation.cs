using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Collections;
using Everywhere.Utilities;
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
/// row, source list, or subscription state. Native branch and turn invalidations are coalesced at
/// this ingress; no subscriptions recursively track the mutable message graph.
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
    public bool HasLaterTurns => _windowEnd < _windowDescriptors.Count;

    /// <summary>
    /// Gets whether the materialized window currently contains the latest turn.
    /// </summary>
    public bool IsAtLatest => !HasLaterTurns;

    /// <summary>
    /// Gets whether this conversation contains displayable messages, independently of its window.
    /// </summary>
    public bool HasMessages => _visibleMessageThreshold > 0;

    /// <summary>
    /// Gets whether the initial single-message UI should give way to the compact conversation UI.
    /// </summary>
    public bool HasMultipleMessages => _visibleMessageThreshold > 1;

    public bool IsWindowOperationActive => _windowOperationCancellation is not null;

    private ChatData Data => _context.Data;

    private readonly ChatContext _context;
    private readonly SourceList<ChatTurnPresentation> _segments = new();
    private readonly Dictionary<object, ChatTurnPresentation> _turns = new(ReferenceEqualityComparer.Instance);
    private readonly DynamicSegmentedList<ChatTurnPresentation, ChatPresentationRow> _visibleRows;

    // Branch descriptors are the next preparation target. The published window keeps its own
    // list reference until preparation succeeds; its bounds must never index the next branch.
    private List<TurnDescriptor> _branchDescriptors = [];
    private List<TurnDescriptor> _windowDescriptors = [];
    private CancellationTokenSource? _windowOperationCancellation;
    private int _visibleMessageThreshold;
    private int _windowStart;
    private int _windowEnd;
    private bool _isDisposed;
    private int _isRepartitionScheduled;

    /// <summary>
    /// Creates the projection containers and branch descriptors without preparing a window.
    /// The manager prepares history before publication; a directly attached view prepares its
    /// initial window through navigation.
    /// </summary>
    /// <param name="context">The runtime owning the presented conversation and its loading.</param>
    public ChatPresentation(ChatContext context)
    {
        _context = context;

        // Keep the chat list physically flat for VariableHeightVirtualizingStackPanel. The
        // segmented list observes each turn independently and translates local insertions to the
        // correct global index without replacing rows owned by other turns.
        _visibleRows = new DynamicSegmentedList<ChatTurnPresentation, ChatPresentationRow>(_segments, segment => segment.Rows, int.MaxValue);
        Rows = _visibleRows.Items;

        Data.BranchChanged += HandleBranchChanged;

        _branchDescriptors = BuildTurnDescriptors();
        RefreshConversationState();
    }

    /// <summary>
    /// Prepares the bounded initial tail, or an already committed window, before a loaded data becomes visible.
    /// </summary>
    public Task<bool> PrepareInitialWindowAsync(CancellationToken cancellationToken = default)
    {
        // Reopening during a branch change resolves the published identities in the target branch,
        // rather than interpreting the old bounds as positions in that branch.
        var (start, end) = GetTargetWindowRange();
        return ChangeWindowAsync(start, end, supersedeCurrentOperation: true, cancellationToken);
    }

    /// <summary>
    /// Preheats and prepends the preceding complete turn batch.
    /// </summary>
    public Task<bool> LoadEarlierAsync(CancellationToken cancellationToken = default)
    {
        if (!HasEarlierTurns || _windowOperationCancellation is not null) return Task.FromResult(false);

        var (start, end) = GetTargetWindowRange();
        start = Math.Max(0, start - TurnBatchSize);
        return ChangeWindowAsync(start, end, supersedeCurrentOperation: false, cancellationToken);
    }

    /// <summary>
    /// Preheats and appends the following complete turn batch.
    /// </summary>
    public Task<bool> LoadLaterAsync(CancellationToken cancellationToken = default)
    {
        if (!HasLaterTurns || _windowOperationCancellation is not null) return Task.FromResult(false);

        var (start, end) = GetTargetWindowRange();
        end = Math.Min(_branchDescriptors.Count, end + TurnBatchSize);
        return ChangeWindowAsync(start, end, supersedeCurrentOperation: false, cancellationToken);
    }

    /// <summary>
    /// Replaces the current navigation window with a bounded, preheated tail window.
    /// </summary>
    public Task<bool> ShowLatestAsync(CancellationToken cancellationToken = default)
    {
        var start = Math.Max(0, _branchDescriptors.Count - TurnBatchSize);
        if (_windowOperationCancellation is null && ReferenceEquals(_windowDescriptors, _branchDescriptors) &&
            _windowStart == start && _windowEnd == _branchDescriptors.Count)
        {
            return Task.FromResult(true);
        }

        return ChangeWindowAsync(start, _branchDescriptors.Count, supersedeCurrentOperation: true, cancellationToken);
    }

    /// <summary>
    /// Shrinks the materialized window around the complete turns intersecting the viewport. Both
    /// rows must already belong to the committed window, so compaction never parses Markdown or
    /// materializes new turns.
    /// </summary>
    public bool CompactAround(ChatPresentationRow firstVisibleRow, ChatPresentationRow lastVisibleRow)
    {
        if (_isDisposed) return false;

        // Visible rows still belong to the old branch while its replacement is preparing.
        // Compaction must not cancel that replacement or republish the old branch's sources.
        if (!ReferenceEquals(_windowDescriptors, _branchDescriptors)) return false;

        var firstVisibleIndex = FindTurnIndex(firstVisibleRow);
        var lastVisibleIndex = FindTurnIndex(lastVisibleRow);
        if (firstVisibleIndex < 0 || lastVisibleIndex < 0) return false;

        var visibleStart = Math.Min(firstVisibleIndex, lastVisibleIndex);
        var visibleEnd = Math.Max(firstVisibleIndex, lastVisibleIndex) + 1;
        var (start, end) = FitWindowAroundRange(visibleStart, visibleEnd, _windowStart, _windowEnd);

        // An earlier edge load may still be preparing a larger range. Once this synchronous
        // compaction commits, that obsolete operation must not expand the window again.
        _windowOperationCancellation?.Cancel();
        if (_windowStart == start && _windowEnd == end) return false;

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
        var targetIndex = FindTurnIndex(_branchDescriptors, node);
        if (targetIndex < 0) return null;

        var (start, end) = FitWindowAroundRange(targetIndex, targetIndex + 1, 0, _branchDescriptors.Count);
        if (_windowOperationCancellation is null &&
            ReferenceEquals(_windowDescriptors, _branchDescriptors) &&
            _windowStart == start && _windowEnd == end &&
            ResolveTargetRow(node, span) is { } existing)
        {
            return existing;
        }

        if (!await ChangeWindowAsync(start, end, supersedeCurrentOperation: true, cancellationToken))
        {
            return null;
        }

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
            // Cancel only the old wait without yielding before this request captures its target.
            // Shared Markdown parsing continues independently of the window operation.
            // ReSharper disable once MethodHasAsyncOverload
            _windowOperationCancellation.Cancel();
        }

        start = Math.Clamp(start, 0, _branchDescriptors.Count);
        end = Math.Clamp(end, start, _branchDescriptors.Count);

        var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _windowOperationCancellation = operationCancellation;
        var preparedTurns = new Dictionary<object, ChatTurnPresentation>(ReferenceEqualityComparer.Instance);
        var branchDescriptors = _branchDescriptors;
        var descriptors = branchDescriptors.AsValueEnumerable().Skip(start).Take(end - start).ToArray();
        var branchRevision = Data.BranchRevision;

        try
        {
            foreach (var descriptor in descriptors)
            {
                if (descriptor.Turn is { } modelTurn)
                {
                    await _context.EnsureTurnLoadedAsync(modelTurn, operationCancellation.Token);
                }
                else
                {
                    await _context.EnsureTurnLoadedAsync(null, operationCancellation.Token);
                }
            }

            if (operationCancellation.IsCancellationRequested || branchRevision != Data.BranchRevision)
            {
                return false;
            }

            foreach (var descriptor in descriptors)
            {
                if (!_turns.TryGetValue(descriptor.Key, out var turn))
                {
                    turn = new ChatTurnPresentation(_context, descriptor.Turn, RefreshConversationState);
                    preparedTurns.Add(descriptor.Key, turn);
                }

                await turn.PrepareMarkdownAsync(descriptor.Nodes, operationCancellation.Token);
            }

            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_isDisposed ||
                    operationCancellation.IsCancellationRequested ||
                    !ReferenceEquals(_windowOperationCancellation, operationCancellation) ||
                    !ReferenceEquals(_branchDescriptors, branchDescriptors) || branchRevision != Data.BranchRevision)
                {
                    return false;
                }

                var replacedTurns = new List<ChatTurnPresentation>();
                foreach (var pair in preparedTurns)
                {
                    if (_turns.TryGetValue(pair.Key, out var replacedTurn))
                    {
                        replacedTurns.Add(replacedTurn);
                    }

                    _turns[pair.Key] = pair.Value;
                }
                preparedTurns.Clear();

                _windowDescriptors = branchDescriptors;
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
                {
                    _windowOperationCancellation = null;
                }

                operationCancellation.Dispose();
            });
        }
    }

    private void HandleBranchChanged(object? sender, EventArgs e) => RequestRepartition();

    private void RequestRepartition()
    {
        // Always enqueue, even on the UI thread: a compound graph mutation publishes one later
        // projection instead of recursively rendering intermediate topology from its own callback.
        if (Interlocked.Exchange(ref _isRepartitionScheduled, 1) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _isRepartitionScheduled, 0);
            Repartition();
        });
    }

    private void Repartition()
    {
        if (_isDisposed) return;

        _branchDescriptors = BuildTurnDescriptors();
        _windowOperationCancellation?.Cancel();

        var (start, end) = GetTargetWindowRange();
        var areBodiesLoaded = _branchDescriptors
            .AsValueEnumerable()
            .Skip(start)
            .Take(end - start)
            .All(descriptor => descriptor.Nodes.AsValueEnumerable().All(node => node.IsMessageLoaded));
        if (areBodiesLoaded && CanSynchronizeWithoutPreheating(start, end))
        {
            _windowDescriptors = _branchDescriptors;
            _windowStart = start;
            _windowEnd = end;
            SynchronizeMaterializedWindow();
            NotifyWindowStateChanged();
            return;
        }

        ApplyRepartitionAsync(start, end).Detach();
    }

    private (int Start, int End) GetTargetWindowRange()
    {
        // Preserve published turn identities, including across multiple superseded preparations.
        // A tail append extends the window; an unrelated branch falls back to its bounded tail.
        var previousKeys = _windowDescriptors
            .AsValueEnumerable()
            .Skip(_windowStart)
            .Take(_windowEnd - _windowStart)
            .Select(static descriptor => descriptor.Key)
            .ToArray();
        if (previousKeys.Length == 0 || !TryResolveContiguousRange(previousKeys, out var start, out var end))
        {
            return (Math.Max(0, _branchDescriptors.Count - TurnBatchSize), _branchDescriptors.Count);
        }

        return (start, _windowEnd == _windowDescriptors.Count ? _branchDescriptors.Count : end);
    }

    private bool CanSynchronizeWithoutPreheating(int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            var descriptor = _branchDescriptors[index];
            if (_turns.TryGetValue(descriptor.Key, out var turn))
            {
                if (!turn.MatchesSources(descriptor.Nodes)) return false;
                continue;
            }

            foreach (var node in descriptor.Nodes)
            {
                if (node.Message is not AssistantChatMessage assistant) continue;
                if (assistant.Items.AsValueEnumerable().OfType<AssistantChatMessageTextSpan>()
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
        RefreshConversationState();
        var desired = new List<ChatTurnPresentation>(_windowEnd - _windowStart);
        var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var index = _windowStart; index < _windowEnd; index++)
        {
            var descriptor = _windowDescriptors[index];
            retained.Add(descriptor.Key);
            if (!_turns.TryGetValue(descriptor.Key, out var turn))
            {
                turn = new ChatTurnPresentation(_context, descriptor.Turn, RefreshConversationState);
                _turns.Add(descriptor.Key, turn);
            }

            turn.UpdateSources(descriptor.Nodes);
            desired.Add(turn);
        }

        ReconcileByReference(_segments, desired);

        foreach (var removed in _turns.AsValueEnumerable().Where(pair => !retained.Contains(pair.Key)).ToArray())
        {
            _turns.Remove(removed.Key);
            removed.Value.Dispose();
        }
    }

    private void RefreshConversationState()
    {
        // Only the zero/one/many distinction is used by the surrounding window controls.
        // Stop after two visible nodes rather than scanning a long history on every token.
        var count = 0;
        foreach (var node in Data.Items)
        {
            if (node.MessageKind is ChatMessageKind.Root or ChatMessageKind.VisualContextReset ||
                node is { IsMessageLoaded: true, Message.IsHidden: true })
            {
                continue;
            }

            if (++count == 2)
            {
                break;
            }
        }

        if (_visibleMessageThreshold == count)
        {
            return;
        }

        _visibleMessageThreshold = count;
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(HasMultipleMessages));
    }

    private bool TryResolveContiguousRange(object[] keys, out int start, out int end)
    {
        start = FindDescriptorIndex(keys[0]);
        if (start < 0)
        {
            end = -1;
            return false;
        }

        for (var offset = 1; offset < keys.Length; offset++)
        {
            var index = start + offset;
            if (index >= _branchDescriptors.Count || !ReferenceEquals(_branchDescriptors[index].Key, keys[offset]))
            {
                end = -1;
                return false;
            }
        }

        end = start + keys.Length;
        return true;
    }

    private int FindDescriptorIndex(object key)
    {
        for (var index = 0; index < _branchDescriptors.Count; index++)
        {
            if (ReferenceEquals(_branchDescriptors[index].Key, key)) return index;
        }

        return -1;
    }

    private static int FindTurnIndex(IReadOnlyList<TurnDescriptor> descriptors, ChatMessageNode node)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (descriptors[index].Nodes.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, node)))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Resolves a visible row through its published presentation owner, even while another branch
    /// is preparing. Pre-user auxiliary rows and rows outside the window have no owning user turn.
    /// </summary>
    public ChatMessageNode? GetUserTurnNode(ChatPresentationRow row)
    {
        return _segments.Items.AsValueEnumerable().FirstOrDefault(turn => turn.ContainsRow(row))?.UserNode;
    }

    private int FindTurnIndex(ChatPresentationRow row)
    {
        var index = _windowStart;
        foreach (var turn in _segments.Items)
        {
            if (turn.ContainsRow(row))
            {
                return index;
            }

            index++;
        }

        return -1;
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
        var index = FindTurnIndex(_windowDescriptors, node);
        if (index < _windowStart || index >= _windowEnd) return null;

        var descriptor = _windowDescriptors[index];
        return _turns.TryGetValue(descriptor.Key, out var turn) ? turn.ResolveTargetRow(node, span) : null;
    }

    private void NotifyWindowStateChanged()
    {
        OnPropertyChanged(nameof(HasEarlierTurns));
        OnPropertyChanged(nameof(HasLaterTurns));
        OnPropertyChanged(nameof(IsAtLatest));
    }

    private List<TurnDescriptor> BuildTurnDescriptors()
    {
        var result = new List<TurnDescriptor>();
        var auxiliary = Data.Items
            .AsValueEnumerable()
            .TakeWhile(node => node.Turn is null)
            .Where(node => !ReferenceEquals(node, Data.RootNode)).ToArray();
        if (auxiliary.Length > 0)
        {
            result.Add(new TurnDescriptor(Data.RootNode, null, auxiliary));
        }

        foreach (var turn in Data.SelectedTurns)
        {
            result.Add(new TurnDescriptor(turn, turn, turn.SelectedNodes));
        }

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
    /// view of the same data.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        DisposeHelper.CancelAndDisposeToDefault(ref _windowOperationCancellation);
        Data.BranchChanged -= HandleBranchChanged;
        _segments.Clear();
        foreach (var turn in _turns.Values) turn.Dispose();
        _turns.Clear();
        _branchDescriptors.Clear();
        _windowDescriptors.Clear();
        _visibleRows.Dispose();
        _segments.Dispose();
    }

    private sealed record TurnDescriptor(object Key, ChatTurn? Turn, IReadOnlyList<ChatMessageNode> Nodes);

    /// <summary>
    /// Owns all presentation state and source subscriptions for one user-delimited turn. Rebuilding
    /// its inexpensive row sequence never rebuilds row objects: role-specific registries retain
    /// them and the visible SourceList receives only a reference-based structural difference.
    /// </summary>
    private sealed class ChatTurnPresentation : IDisposable
    {
        private ChatData Data => _context.Data;

        public IObservableList<ChatPresentationRow> Rows => _visibleRows;

        public ChatMessageNode? UserNode => _turn?.UserNode;

        public async Task PrepareMarkdownAsync(IReadOnlyList<ChatMessageNode> nodes, CancellationToken cancellationToken)
        {
            foreach (var node in nodes)
            {
                if (node.Message is not AssistantChatMessage assistant)
                {
                    continue;
                }

                foreach (var span in assistant.Items.OfType<AssistantChatMessageTextSpan>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = (AssistantTextOutputPresentationRow)GetOutputRow(node, span);
                    try
                    {
                        await row.Markdown.PrepareAsync(cancellationToken);
                    }
                    catch (ObjectDisposedException) when (!assistant.Items.Contains(span))
                    {
                        // Request retry can truncate provisional spans during preparation. Their
                        // caches no longer belong to the target; do not fail the surviving window.
                    }
                }
            }
        }

        private readonly Dictionary<ChatMessageNode, ProcessSummaryPresentationRow> _summaryRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ProcessSummaryPresentationRow, List<ChatPresentationRow>> _summaryContents = new();
        private readonly Dictionary<ChatBusyActivity, BusyActivityItemPresentationRow> _busyRows = new(ReferenceEqualityComparer.Instance);
        private readonly Action _conversationStateChanged;
        private readonly ChatTurn? _turn;
        private readonly ChatContext _context;

        private static readonly IDynamicLocaleKey ReasoningHeader = new DynamicLocaleKey(LocaleKey.ChatMessageControl_Assistant_Reasoning);
        private static readonly IDynamicLocaleKey GenericHeader = new DynamicLocaleKey(LocaleKey.ChatPresentation_GenericActivity);

        /// <summary>
        /// Completed activity collections at or below this size are shown as direct activity rows.
        /// Larger collections keep their aggregate container to avoid overwhelming the conversation.
        /// </summary>
        private const int InlineActivityLimit = 1;

        private readonly SourceList<ChatPresentationRow> _visibleRows = new();
        private readonly Dictionary<ChatMessageNode, ChatMessagePresentationRow> _messageRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, ActivityItemPresentationRow> _activityRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<object, ActivityGroupPresentationRow> _groupRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<AssistantChatMessageSpan, AssistantOutputPresentationRow> _outputRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, PendingAssistantPresentationRow> _pendingRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, TurnFooterPresentationRow> _footerRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, NoResponsePresentationRow> _noResponseRows = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<ChatMessageNode, AssistantErrorPresentationRow> _errorRows = new();
        private readonly Dictionary<ChatMessageNode, AssistantCanceledPresentationRow> _canceledRows = new();
        private readonly HashSet<ActivityGroupPresentationRow> _groupsAwaitingFinalPlacement = new(ReferenceEqualityComparer.Instance);

        private IReadOnlyList<ChatMessageNode> _nodes = [];
        private IReadOnlyList<ChatPresentationRow> _projectedRows = [];
        private IReadOnlyList<BusyActivityItemPresentationRow> _busyActivities = [];
        private bool _isDisposed;

        // Only this ingress flag crosses threads. Rows, caches and collections remain UI-owned.
        private int _isRefreshPosted;

        public ChatTurnPresentation(ChatContext context, ChatTurn? turn, Action conversationStateChanged)
        {
            _context = context;
            _turn = turn;
            _conversationStateChanged = conversationStateChanged;
            if (turn is not null)
            {
                turn.Invalidated += HandleInvalidated;
            }
            else
            {
                Data.AuxiliaryInvalidated += HandleInvalidated;
            }
        }

        public bool MatchesSources(IReadOnlyList<ChatMessageNode> nodes)
        {
            return ReferencesEqual(_nodes, nodes);
        }

        public bool ContainsRow(ChatPresentationRow row)
        {
            return _visibleRows.Items.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, row));
        }

        public void UpdateSources(IReadOnlyList<ChatMessageNode> nodes)
        {
            _nodes = nodes;
            RefreshFromSources();
        }

        private static bool ReferencesEqual<T>(IReadOnlyList<T> first, IReadOnlyList<T> second) where T : class
        {
            return first.Count == second.Count && first.AsValueEnumerable().Zip(second).All(pair => ReferenceEquals(pair.First, pair.Second));
        }

        private void HandleInvalidated(object? sender, EventArgs e) => RequestRefresh();

        private void RequestRefresh()
        {
            if (Interlocked.Exchange(ref _isRefreshPosted, 1) != 0)
            {
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                // Clear before capture. An invalidation during this finite pass schedules the next
                // dispatcher turn, so sustained output cannot starve input or lose its final update.
                Interlocked.Exchange(ref _isRefreshPosted, 0);
                RefreshFromSources();
            });
        }

        private void RefreshFromSources()
        {
            if (_isDisposed)
            {
                return;
            }

            // Preheating can already own rows for the next path. Do not prune them using the old
            // committed sources (or an uninitialized window); UpdateSources refreshes on publication.
            if (_nodes.Count == 0 || _turn is not null && !MatchesSources(_turn.SelectedNodes))
            {
                return;
            }

            var capturedRevision = _turn?.Revision ?? 0;
            _conversationStateChanged();
            var activities = _turn?.Activities.AsValueEnumerable().Where(activity => _nodes.Contains(activity.Owner)).ToArray() ?? [];
            foreach (var removed in _busyRows.Keys.AsValueEnumerable().Except(activities).ToArray())
            {
                _busyRows.Remove(removed);
            }

            _busyActivities = activities.AsValueEnumerable().Select(activity =>
            {
                if (!_busyRows.TryGetValue(activity, out var row))
                {
                    row = new BusyActivityItemPresentationRow(activity);
                    _busyRows.Add(activity, row);
                }

                row.Refresh();
                return row;
            }).ToArray();

            var activeSpans = _nodes
                .AsValueEnumerable()
                .Select(node => node.Message)
                .OfType<AssistantChatMessage>()
                .SelectMany(assistant => assistant.Items)
                .ToHashSet();
            var activeActivities = activeSpans
                .AsValueEnumerable()
                .OfType<AssistantChatMessageFunctionCallSpan>()
                .SelectMany(span => span.Items)
                .Cast<object>()
                .Concat(activeSpans.OfType<AssistantChatMessageReasoningSpan>())
                .Concat(activities)
                .ToHashSet(ReferenceEqualityComparer.Instance);
            foreach (var removed in _activityRows.Keys.AsValueEnumerable().Where(source => !activeActivities.Contains(source)).ToArray())
            {
                _activityRows.Remove(removed);
            }

            foreach (var removed in _outputRows.Keys.AsValueEnumerable().Where(span => !activeSpans.Contains(span)).ToArray())
            {
                if (_outputRows.Remove(removed, out var row) && row is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            foreach (var removed in _groupRows.Keys.AsValueEnumerable().Where(source => !activeActivities.Contains(source)).ToArray())
            {
                if (_groupRows.Remove(removed, out var group))
                {
                    _groupsAwaitingFinalPlacement.Remove(group);
                }
            }

            RebuildVisibleRows();
            foreach (var row in _outputRows.Values.AsValueEnumerable().OfType<AssistantTextOutputPresentationRow>())
            {
                row.Markdown.Refresh();
            }

            if (_turn is not null && _turn.Revision != capturedRevision)
            {
                RequestRefresh();
            }
        }

        private void HandleSummaryPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ProcessSummaryPresentationRow.IsExpanded))
            {
                ReconcileVisibleRows();
            }
        }

        private void ReconcileVisibleRows()
        {
            var desired = new List<ChatPresentationRow>();
            foreach (var row in _projectedRows)
            {
                desired.Add(row);
                if (row is ProcessSummaryPresentationRow { IsExpanded: true } summary && _summaryContents.TryGetValue(summary, out var contents))
                {
                    desired.AddRange(contents);
                }
            }

            ReconcileByReference(_visibleRows, desired);
        }

        private void RebuildVisibleRows()
        {
            if (_isDisposed) return;

            var desired = new List<ChatPresentationRow>();
            var assistants = new List<ChatMessageNode>();
            _summaryContents.Clear();
            var continuationTarget = Data.ContinuationTarget;
            foreach (var node in _nodes)
            {
                if (node.Message.IsHidden)
                {
                    continue;
                }

                if (node.Message is AssistantChatMessage)
                {
                    assistants.Add(node);
                    continue;
                }

                AppendAssistantRun(assistants.ToArray(), desired, continuationTarget);
                assistants.Clear();
                desired.Add(GetMessageRow(node));
                desired.AddRange(_busyActivities.Where(activity => ReferenceEquals(activity.Owner, node)));
            }

            AppendAssistantRun(assistants.ToArray(), desired, continuationTarget);
            _projectedRows = desired;
            ReconcileVisibleRows();
        }

        private void AppendAssistantRun(ChatMessageNode[] assistants, List<ChatPresentationRow> desired, ChatMessageNode? continuationTarget)
        {
            if (assistants.Length == 0)
            {
                return;
            }

            var latestNode = assistants.AsValueEnumerable().LastOrDefault();
            var latest = latestNode?.Message as AssistantChatMessage;
            var isRunning = latest?.IsBusy is true;
            var rows = RefreshAssistantRows(assistants, includeLatestError: isRunning, keepTrailingActivityOpen: isRunning);

            if (isRunning)
            {
                AppendRows(rows, desired, false);
                if (latestNode is not null && latest is { Count: 0 } &&
                    !_busyActivities.AsValueEnumerable().Any(activity => activity.IsRunning && ReferenceEquals(activity.Owner, latestNode)))
                {
                    desired.Add(GetPendingRow(latestNode));
                }

                if (latestNode is not null)
                {
                    desired.Add(GetFooterRow(latestNode));
                }

                return;
            }

            if (latestNode is null || latest is null)
            {
                AppendRows(rows, desired, false);
                return;
            }

            if (latest.IsCanceled)
            {
                AppendCompletedProcess(assistants[0], rows, desired);
                desired.Add(GetCanceledRow(latestNode, ReferenceEquals(latestNode, continuationTarget)));
                desired.Add(GetFooterRow(latestNode));
                return;
            }

            if (latest.ErrorMessageKey is not null)
            {
                AppendCompletedProcess(assistants[0], rows, desired);
                desired.Add(GetErrorRow(latestNode, ReferenceEquals(latestNode, continuationTarget)));
                desired.Add(GetFooterRow(latestNode));
                return;
            }

            var finalStart = FindFinalOutputStart(rows, latestNode);
            var process = finalStart < 0 ? rows : [.. rows.AsValueEnumerable().Take(finalStart)];
            var final = finalStart < 0 ? [] : rows.AsValueEnumerable().Skip(finalStart).ToArray();
            AppendCompletedProcess(assistants[0], process, desired);
            AppendRows(final, desired, true);

            if (final.Length == 0) desired.Add(GetNoResponseRow(latestNode));
            desired.Add(GetFooterRow(latestNode));
        }

        private List<ChatPresentationRow> RefreshAssistantRows(ChatMessageNode[] assistants, bool includeLatestError, bool keepTrailingActivityOpen)
        {
            var result = new List<ChatPresentationRow>();
            var pending = new List<ActivityItemPresentationRow>();

            void Flush(bool isAwaitingContinuation)
            {
                if (pending.Count == 0) return;
                var group = GetGroupRow(pending[0].Source);
                if (group.UpdateItems(pending, isAwaitingContinuation)) ScheduleFinalPlacement(group);
                result.Add(group);
                pending.Clear();
            }

            void AppendBusyActivities(ChatMessageNode node, AssistantChatMessageSpan? anchorSpan)
            {
                foreach (var activity in _busyActivities.AsValueEnumerable())
                {
                    if (ReferenceEquals(activity.Owner, node) && ReferenceEquals(activity.AnchorSpan, anchorSpan))
                    {
                        pending.Add(activity);
                    }
                }
            }

            for (var assistantIndex = 0; assistantIndex < assistants.Length; assistantIndex++)
            {
                if (assistantIndex > 0) Flush(isAwaitingContinuation: false);
                var node = assistants[assistantIndex];
                var assistant = (AssistantChatMessage)node.Message;
                AppendBusyActivities(node, null);
                foreach (var span in assistant.Items.AsValueEnumerable().OfType<AssistantChatMessageSpan>())
                {
                    switch (span)
                    {
                        case AssistantChatMessageReasoningSpan reasoning:
                        {
                            var row = GetReasoningRow(assistant, reasoning);
                            row.Refresh();
                            pending.Add(row);
                            break;
                        }
                        case AssistantChatMessageFunctionCallSpan functionSpan:
                        {
                            foreach (var function in functionSpan.Items)
                            {
                                var row = GetFunctionRow(function);
                                row.Refresh();
                                pending.Add(row);
                            }
                            break;
                        }
                        case AssistantChatMessageTextSpan:
                        case AssistantChatMessageImageSpan:
                        {
                            Flush(isAwaitingContinuation: false);
                            result.Add(GetOutputRow(node, span));
                            break;
                        }
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
                if (assistant.IsCanceled && assistantIndex < assistants.Length - 1)
                {
                    result.Add(GetCanceledRow(node, false));
                }
                if (assistant.ErrorMessageKey is not null && (assistantIndex < assistants.Length - 1 || includeLatestError))
                {
                    result.Add(GetErrorRow(node, false));
                }
            }

            return result;
        }

        private void AppendCompletedProcess(ChatMessageNode firstNode, IReadOnlyList<ChatPresentationRow> rows, List<ChatPresentationRow> desired)
        {
            if (rows.Count == 0) return;
            var items = rows.AsValueEnumerable().OfType<ActivityGroupPresentationRow>().SelectMany(group => group.Items).ToArray();
            if (!_summaryRows.TryGetValue(firstNode, out var summaryRow))
            {
                summaryRow = new ProcessSummaryPresentationRow();
                summaryRow.PropertyChanged += HandleSummaryPropertyChanged;
                _summaryRows.Add(firstNode, summaryRow);
            }

            summaryRow.UpdateStatistics(ChatActivityStatistics.Calculate(items));

            // Do not replace a just-completed running card with the final process summary while its
            // glow is still fading. The delayed callback rebuilds this turn after the visual morph,
            // at which point the normal direct-row or summary rule is applied.
            if (rows.AsValueEnumerable().OfType<ActivityGroupPresentationRow>().Any(group => _groupsAwaitingFinalPlacement.Contains(group)) ||
                items.Length is > 0 and <= InlineActivityLimit)
            {
                AppendRows(rows, desired, false);
                return;
            }

            desired.Add(summaryRow);
            // Cache projected children even while collapsed. Expansion changes only visibility;
            // it does not recapture model values or replay activity completion transitions.
            var contents = new List<ChatPresentationRow>();
            AppendRows(rows, contents, false);
            _summaryContents.Add(summaryRow, contents);
        }

        private static int FindFinalOutputStart(IReadOnlyList<ChatPresentationRow> rows, ChatMessageNode latest)
        {
            var index = rows.Count;
            while (index > 0 && rows[index - 1] is AssistantOutputPresentationRow output && ReferenceEquals(output.AssistantNode, latest))
            {
                index--;
            }

            return index == rows.Count ? -1 : index;
        }

        private void AppendRows(IReadOnlyList<ChatPresentationRow> rows, List<ChatPresentationRow> desired, bool isFinal)
        {
            foreach (var row in rows)
            {
                switch (row)
                {
                    case ActivityGroupPresentationRow group:
                    {
                        // Every running segment remains a Group, even when it currently contains a
                        // single item, so the user receives the glow and live status treatment. A
                        // completed Group is held in that shape for one short transition window;
                        // afterwards a single item is promoted directly and larger groups retain
                        // their identity and user-controlled expansion state.
                        if (!group.IsRunning &&
                            !_groupsAwaitingFinalPlacement.Contains(group) &&
                            group.Items.Count <= InlineActivityLimit)
                        {
                            desired.AddRange(group.Items);
                            break;
                        }

                        desired.Add(group);
                        break;
                    }
                    case AssistantOutputPresentationRow output:
                    {
                        output.IsFinal = isFinal;
                        desired.Add(output);
                        break;
                    }
                    default:
                    {
                        desired.Add(row);
                        break;
                    }
                }
            }
        }

        private ChatMessagePresentationRow GetMessageRow(ChatMessageNode node)
        {
            if (!_messageRows.TryGetValue(node, out var row))
            {
                row = new ChatMessagePresentationRow(_context, node);
                _messageRows.Add(node, row);
            }

            row.Refresh();
            return row;
        }

        private PendingAssistantPresentationRow GetPendingRow(ChatMessageNode node) =>
            _pendingRows.GetValueOrDefault(node) ?? (_pendingRows[node] = new PendingAssistantPresentationRow());

        private TurnFooterPresentationRow GetFooterRow(ChatMessageNode node)
        {
            if (!_footerRows.TryGetValue(node, out var row))
            {
                row = new TurnFooterPresentationRow(_context, node);
                _footerRows.Add(node, row);
            }

            row.Refresh();
            return row;
        }

        private NoResponsePresentationRow GetNoResponseRow(ChatMessageNode node) =>
            _noResponseRows.GetValueOrDefault(node) ?? (_noResponseRows[node] = new NoResponsePresentationRow());

        private AssistantCanceledPresentationRow GetCanceledRow(ChatMessageNode node, bool isContinuationTarget)
        {
            if (!_canceledRows.TryGetValue(node, out var row))
            {
                row = new AssistantCanceledPresentationRow(node);
                _canceledRows.Add(node, row);
            }

            row.IsContinuationTarget = isContinuationTarget;
            return row;
        }

        private AssistantErrorPresentationRow GetErrorRow(ChatMessageNode node, bool isContinuationTarget)
        {
            if (!_errorRows.TryGetValue(node, out var row))
            {
                row = new AssistantErrorPresentationRow(node);
                _errorRows.Add(node, row);
            }

            row.IsContinuationTarget = isContinuationTarget;
            row.Refresh();
            return row;
        }

        public ChatPresentationRow? ResolveTargetRow(ChatMessageNode node, AssistantChatMessageSpan? span)
        {
            if (span is null)
            {
                return _messageRows.GetValueOrDefault(node);
            }

            if (!_outputRows.TryGetValue(span, out var row)) return null;
            if (_visibleRows.Items.AsValueEnumerable().Any(candidate => ReferenceEquals(candidate, row))) return row;

            // A failed partial output can live inside a collapsed process summary. Search navigation
            // explicitly targets that output, so reveal the existing rows rather than manufacturing
            // a parallel presentation path for the same span.
            foreach (var (summary, contents) in _summaryContents)
            {
                if (!contents.Contains(row))
                {
                    continue;
                }

                summary.IsExpanded = true;
                return row;
            }

            return null;
        }

        private AssistantOutputPresentationRow GetOutputRow(ChatMessageNode node, AssistantChatMessageSpan span)
        {
            var row = _outputRows.GetValueOrDefault(span) ?? (_outputRows[span] = span switch
            {
                AssistantChatMessageTextSpan text => new AssistantTextOutputPresentationRow(node, text),
                AssistantChatMessageImageSpan image => new AssistantImageOutputPresentationRow(node, image),
                _ => throw new InvalidOperationException($"Unexpected span type {span.GetType().Name}")
            });

            if (row is AssistantImageOutputPresentationRow imageRow)
            {
                imageRow.Refresh();
            }

            return row;
        }

        private ActivityGroupPresentationRow GetGroupRow(object source)
        {
            return _groupRows.GetValueOrDefault(source) ?? (_groupRows[source] = new ActivityGroupPresentationRow());
        }

        private ActivityItemPresentationRow GetReasoningRow(AssistantChatMessage assistant, AssistantChatMessageReasoningSpan reasoning)
        {
            if (!_activityRows.TryGetValue(reasoning, out var row))
            {
                row = new ReasoningActivityItemPresentationRow(assistant, reasoning, ReasoningHeader);
                _activityRows.Add(reasoning, row);
            }

            return row;
        }

        private ActivityItemPresentationRow GetFunctionRow(FunctionCallChatMessage function)
        {
            if (!_activityRows.TryGetValue(function, out var row))
            {
                row = new FunctionCallActivityItemPresentationRow(function, GenericHeader);
                _activityRows.Add(function, row);
            }

            return row;
        }

        private void ScheduleFinalPlacement(ActivityGroupPresentationRow group)
        {
            if (!_groupsAwaitingFinalPlacement.Add(group)) return;

            DispatcherTimer.RunOnce(
                () =>
                {
                    if (_isDisposed) return;
                    _groupsAwaitingFinalPlacement.Remove(group);

                    // A new activity can join the same segment during the transition window. In that
                    // case the Group retains its state and its next real completion will schedule a new
                    // final-placement pass.
                    if (group.IsRunning) return;

                    RequestRefresh();
                },
                TimeSpan.FromMilliseconds(400)); // Leaves a just-completed running Group in place long enough for the 320 ms glow transition
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_turn is not null)
            {
                _turn.Invalidated -= HandleInvalidated;
            }
            else
            {
                Data.AuxiliaryInvalidated -= HandleInvalidated;
            }

            _groupsAwaitingFinalPlacement.Clear();
            foreach (var summary in _summaryRows.Values)
            {
                summary.PropertyChanged -= HandleSummaryPropertyChanged;
            }

            _summaryContents.Clear();
            _projectedRows = [];
            foreach (var row in _outputRows.Values.AsValueEnumerable().OfType<IDisposable>())
            {
                row.Dispose();
            }

            _outputRows.Clear();
            _visibleRows.Dispose();
        }
    }
}
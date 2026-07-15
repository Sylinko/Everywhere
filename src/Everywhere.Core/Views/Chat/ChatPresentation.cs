using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Disposables;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DynamicData;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Lucide.Avalonia;
using ZLinq;

namespace Everywhere.Views;

/// <summary>
/// Incrementally projects the current chat branch into stable, flat presentation rows. Top-level
/// turns and their child row lists retain identity; a positional segmented list applies their
/// DynamicData changes without recreating unaffected rows or controls.
/// </summary>
public sealed class ChatPresentation
{
    /// <summary>
    /// Gets the stable, flat list consumed by the outer virtualizing chat ItemsControl. Expansion
    /// state and first-presentation state live on these rows for the lifetime of the ChatContext.
    /// </summary>
    public IReadOnlyBindableList<ChatPresentationRow> Rows { get; }

    private readonly ChatContext _context;
    private readonly SourceList<IChatPresentationSegment> _segments = new();
    private readonly Dictionary<object, ChatTurnPresentation> _turns = new(ReferenceEqualityComparer.Instance);
    private readonly List<BusyActivityItemPresentationRow> _busyActivities = [];
    private readonly DynamicSegmentedList<IChatPresentationSegment, ChatPresentationRow> _visibleRows;
    private readonly CompositeDisposable _disposables = new();
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
        _visibleRows = new DynamicSegmentedList<IChatPresentationSegment, ChatPresentationRow>(_segments, segment => segment.Rows);
        Rows = _visibleRows.Items;

        _disposables.Add(context.ConnectDisplayItems().Subscribe(_ => RequestRepartition()));

        Repartition();
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
    /// Adds a runtime-only activity to the current assistant turn. The returned scope completes the
    /// stable activity row; it does not remove it, allowing the current in-memory presentation to
    /// retain an accurate chronology while persisted messages remain completely unchanged.
    /// </summary>
    internal IDisposable SetBusyActivity(LucideIconKind icon, IDynamicLocaleKey headerKey)
    {
        ArgumentNullException.ThrowIfNull(headerKey);
        var scope = new BusyActivityScope(this, new BusyActivityItemPresentationRow(icon, headerKey, DateTimeOffset.UtcNow));
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

            // SetBusyActivity is normally entered after ChatService has appended the busy assistant
            // node. If a future caller violates that ordering, silently omit the visual activity
            // instead of manufacturing a message node or weakening the persistence boundary.
            if (assistantNode?.Message is not AssistantChatMessage assistant) return;

            scope.Row.AssistantNode = assistantNode;
            scope.Row.AnchorSpan = assistant.Spans.LastOrDefault();
            _busyActivities.Add(scope.Row);
        }

        if (scope.FinishedAt is { } finishedAt) scope.Row.Complete(finishedAt);
        Repartition();
    }

    private void Repartition()
    {
        if (_isDisposed) return;

        var descriptors = BuildTurnDescriptors(_context.Items.AsValueEnumerable().Where(node => node.Message is not RootChatMessage).ToList());
        var desired = new List<IChatPresentationSegment>(descriptors.Count);

        var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var descriptor in descriptors.AsValueEnumerable())
        {
            retained.Add(descriptor.Key);
            if (!_turns.TryGetValue(descriptor.Key, out var turn))
            {
                turn = new ChatTurnPresentation();
                _turns.Add(descriptor.Key, turn);
            }

            turn.UpdateSources(
                descriptor.Nodes,
                _busyActivities
                    .AsValueEnumerable()
                    .Where(activity => descriptor.Nodes.AsValueEnumerable().Any(node => ReferenceEquals(node, activity.AssistantNode)))
                    .ToList());
            desired.Add(turn);
        }

        ReconcileByReference(_segments, desired);

        foreach (var removed in _turns.AsValueEnumerable().Where(pair => !retained.Contains(pair.Key)).ToList())
        {
            _turns.Remove(removed.Key);
            removed.Value.Dispose();
        }
    }

    private static List<TurnDescriptor> BuildTurnDescriptors(List<ChatMessageNode> nodes)
    {
        var result = new List<TurnDescriptor>();
        List<ChatMessageNode>? current = null;
        object? currentKey = null;

        void Flush()
        {
            if (current is not { Count: > 0 } || currentKey is null) return;
            result.Add(new TurnDescriptor(currentKey, current.ToArray()));
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
    internal void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _disposables.Dispose();
        _segments.Clear();
        foreach (var turn in _turns.Values) turn.Dispose();
        _turns.Clear();
        _busyActivities.Clear();
        _visibleRows.Dispose();
        _segments.Dispose();
    }

    private sealed record TurnDescriptor(object Key, IReadOnlyList<ChatMessageNode> Nodes);

    private interface IChatPresentationSegment : IDisposable
    {
        IObservableList<ChatPresentationRow> Rows { get; }
    }

    /// <summary>
    /// Thread-safe lifetime token returned to background chat operations. Only the completion
    /// timestamp crosses threads; all row attachment and SourceList work is dispatched through the
    /// owning presentation on Avalonia's UI thread.
    /// </summary>
    private sealed class BusyActivityScope(
        ChatPresentation owner,
        BusyActivityItemPresentationRow row
    ) : IDisposable
    {
        private long _finishedAtUtcTicks;

        public BusyActivityItemPresentationRow Row { get; } = row;
        public bool IsAttached { get; set; }

        public DateTimeOffset? FinishedAt
        {
            get
            {
                var ticks = Interlocked.Read(ref _finishedAtUtcTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

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
        private static readonly IDynamicLocaleKey GenericHeader = new DynamicLocaleKey(LocaleKey.ChatActivity_GenericActivity);

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
        private int _isRefreshScheduled;

        public IObservableList<ChatPresentationRow> Rows => _visibleRows;
        private ProcessSummaryPresentationRow SummaryRow => field ??= new ProcessSummaryPresentationRow(RowsChanged);

        /// <summary>
        /// Replaces the turn's persisted node view and runtime-only activity view by reference.
        /// Persisted membership changes require subscription rewiring; a transient activity change
        /// only requires a cheap structural rebuild because those rows are completed explicitly by
        /// their owning scope.
        /// </summary>
        public void UpdateSources(IReadOnlyList<ChatMessageNode> nodes, IReadOnlyList<BusyActivityItemPresentationRow> busyActivities)
        {
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

            // Collection membership changes are much less frequent than streamed property changes.
            // Rebuilding this turn-local subscription set keeps removal/disposal exact without
            // maintaining a second nested ownership graph; it never touches another turn's rows.
            _subscriptions.Dispose();
            _subscriptions = new CompositeDisposable();
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            foreach (var node in _nodes.AsValueEnumerable())
            {
                SubscribeProperties(node.Message, visited);
                if (node.Message is not AssistantChatMessage assistant) continue;
                SubscribeCollection(assistant.Spans, visited);
                foreach (var span in assistant.Spans.AsValueEnumerable())
                {
                    SubscribeProperties(span, visited);
                    if (span is not AssistantChatMessageFunctionCallSpan functions) continue;
                    SubscribeCollection(functions.FunctionCalls, visited);
                    foreach (var function in functions.FunctionCalls.AsValueEnumerable())
                    {
                        SubscribeProperties(function, visited);
                        SubscribeCollection(function.DisplayBlocks, visited);
                        foreach (var block in function.DisplayBlocks.AsValueEnumerable()) SubscribeBlock(block, visited);
                    }
                }
            }

            RebuildVisibleRows();
        }

        private void SubscribeBlock(ChatPluginDisplayBlock block, HashSet<object> visited)
        {
            if (!visited.Add(block)) return;

            block.PropertyChanged += HandlePropertyChanged;
            _subscriptions.Add(Disposable.Create(() => block.PropertyChanged -= HandlePropertyChanged));
            if (block is not ChatPluginContainerDisplayBlock container) return;

            SubscribeCollection(container.Children, visited);
            foreach (var child in container.Children) SubscribeBlock(child, visited);
        }

        private void SubscribeCollection(object source, HashSet<object> visited)
        {
            if (!visited.Add(source) || source is not INotifyCollectionChanged collection) return;

            collection.CollectionChanged += HandleCollectionChanged;
            _subscriptions.Add(Disposable.Create(() => collection.CollectionChanged -= HandleCollectionChanged));
        }

        private void SubscribeProperties(ObservableObject source, HashSet<object> visited)
        {
            if (!visited.Add(source)) return;

            source.PropertyChanged += HandlePropertyChanged;
            _subscriptions.Add(Disposable.Create(() => source.PropertyChanged -= HandlePropertyChanged));
        }

        private void HandleCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // Span, function-call, and display-block membership is normally produced by the worker
            // that executes ChatService. Membership changes are much less frequent than streamed
            // property updates, so PostOnDemand keeps the ownership rule obvious without adding a
            // second coalescing state machine or delaying changes already raised on the UI thread.
            Dispatcher.UIThread.PostOnDemand(Rewire);
        }

        private void HandlePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Model/tool completion may be raised from the worker executing the chat. Collapse a
            // burst into one UI-thread projection refresh rather than posting once per streamed
            // property notification. PostOnDemand preserves synchronous UI-thread updates.
            if (Interlocked.Exchange(ref _isRefreshScheduled, 1) == 0)
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    Interlocked.Exchange(ref _isRefreshScheduled, 0);
                    RefreshFromSources();
                });
            }
        }

        private void RefreshFromSources()
        {
            if (_isDisposed) return;

            // Rows read most values directly from source objects. Refresh their bindings first, then
            // reconcile structural state in case completion promoted output or changed a group.
            foreach (var activity in _activityRows.Values) activity.Refresh();
            RebuildVisibleRows();
        }

        private void RowsChanged() => RebuildVisibleRows();

        private void RebuildVisibleRows()
        {
            if (_isDisposed) return;

            var desired = _nodes.Where(node => node.Message is not AssistantChatMessage).Select(GetMessageRow).Cast<ChatPresentationRow>().ToList();
            var assistants = _nodes.AsValueEnumerable().Where(node => node.Message is AssistantChatMessage).ToList();
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
                AppendCompletedProcess(BuildEntries(
                    assistants,
                    includeLatestError: false,
                    keepTrailingActivityOpen: false), desired);
                desired.Add(GetErrorRow(latestNode, true));
                desired.Add(GetFooterRow(latestNode));
                ReconcileByReference(_visibleRows, desired);
                return;
            }

            var finalStart = FindFinalOutputStart(entries, latestNode);
            var process = finalStart < 0 ? entries : entries.Take(finalStart).ToList();
            var final = finalStart < 0 ? [] : entries.Skip(finalStart).ToList();
            AppendCompletedProcess(process, desired);
            AppendEntries(final, desired, true);

            if (final.Count == 0) desired.Add(GetNoResponseRow(latestNode));
            desired.Add(GetFooterRow(latestNode));
            ReconcileByReference(_visibleRows, desired);
        }

        private List<Entry> BuildEntries(
            List<ChatMessageNode> assistants,
            bool includeLatestError,
            bool keepTrailingActivityOpen)
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

            for (var assistantIndex = 0; assistantIndex < assistants.Count; assistantIndex++)
            {
                if (assistantIndex > 0) Flush(isAwaitingContinuation: false);
                var node = assistants[assistantIndex];
                var assistant = (AssistantChatMessage)node.Message;
                AppendBusyActivities(node, null);
                foreach (var span in assistant.Spans.AsValueEnumerable())
                {
                    switch (span)
                    {
                        case AssistantChatMessageReasoningSpan reasoning:
                            pending.Add(GetReasoningRow(reasoning));
                            break;
                        case AssistantChatMessageFunctionCallSpan functionSpan:
                            pending.AddRange(functionSpan.FunctionCalls.Select(GetFunctionRow));
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
                Flush(keepTrailingActivityOpen && assistantIndex == assistants.Count - 1);
                if (assistant.ErrorMessageKey is not null && (assistantIndex < assistants.Count - 1 || includeLatestError))
                {
                    result.Add(new ErrorEntry(GetErrorRow(node, false)));
                }
            }

            return result;
        }

        private void AppendCompletedProcess(IReadOnlyList<Entry> entries, List<ChatPresentationRow> desired)
        {
            if (entries.Count == 0) return;
            var items = entries.OfType<GroupEntry>().SelectMany(entry => entry.Group.Items).ToList();
            SummaryRow.UpdateStatistics(ChatActivityStatistics.Calculate(items));

            // Do not replace a just-completed running card with the final process summary while its
            // glow is still fading. The delayed callback rebuilds this turn after the visual morph,
            // at which point the normal direct-row or summary rule is applied.
            if (entries.OfType<GroupEntry>().Any(entry => _groupsAwaitingFinalPlacement.Contains(entry.Group)) ||
                items.Count is > 0 and <= ChatActivityPresentationPolicy.InlineActivityLimit)
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

        private void AppendEntries(
            IReadOnlyList<Entry> entries,
            List<ChatPresentationRow> desired,
            bool isFinal)
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
                            group.Group.Items.Count <= ChatActivityPresentationPolicy.InlineActivityLimit)
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

        private AssistantOutputPresentationRow GetOutputRow(ChatMessageNode node, AssistantChatMessageSpan span) =>
            _outputRows.GetValueOrDefault(span) ?? (_outputRows[span] = new AssistantOutputPresentationRow(node, span));

        private ActivityGroupPresentationRow GetGroupRow(object source) =>
            _groupRows.GetValueOrDefault(source) ?? (_groupRows[source] = new ActivityGroupPresentationRow());

        private ActivityItemPresentationRow GetReasoningRow(AssistantChatMessageReasoningSpan reasoning) =>
            _activityRows.GetValueOrDefault(reasoning) ??
            (_activityRows[reasoning] = new ReasoningActivityItemPresentationRow(reasoning, ReasoningHeader));

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
                ChatActivityPresentationPolicy.CompletionPlacementDelay);
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
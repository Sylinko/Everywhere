using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Base class for stable, presentation-only rows. Row identity is ordinary reference identity;
/// source changes update properties on the existing instance rather than replacing the row.
/// </summary>
public abstract class ChatPresentationRow : ObservableObject
{
    private bool _hasBeenPresented;

    /// <summary>
    /// Returns true only for the first realization of this stable row. Virtualization recycling does
    /// not replay insertion animation for a row that has already appeared.
    /// </summary>
    internal bool TryMarkPresented() => !_hasBeenPresented && (_hasBeenPresented = true);
}

/// <summary>
/// Displays an existing non-assistant message without copying its state.
/// </summary>
public class ChatMessagePresentationRow(ChatMessageNode node) : ChatPresentationRow
{
    public ChatMessageNode Node { get; } = node;
}

/// <summary>
/// Represents the lightweight skeleton shown before a busy assistant emits a span.
/// </summary>
public sealed class PendingAssistantPresentationRow : ChatPresentationRow;

/// <summary>
/// Owns the completed-turn process expansion state and its aggregate activity statistics.
/// </summary>
public sealed class ProcessSummaryPresentationRow(Action rowsChanged) : ChatPresentationRow
{
    /// <summary>
    /// Gets the latest whole-turn statistics snapshot. Keeping the counters under one property makes
    /// the binding surface stable when new activity metrics are introduced.
    /// </summary>
    public ChatActivityStatistics Statistics => _statistics;

    public bool IsExpanded
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            rowsChanged();
        }
    }

    private ChatActivityStatistics _statistics;

    /// <summary>
    /// Replaces the aggregate snapshot when the turn projection changes.
    /// </summary>
    public void UpdateStatistics(ChatActivityStatistics statistics) =>
        SetProperty(ref _statistics, statistics, nameof(Statistics));
}

/// <summary>
/// Base for stable activity rows. Templates bind directly to each row's observable source;
/// shared getters supply the grouping algorithm with timing and liveness without forwarding
/// source notifications. Expansion is presentation-only and belongs to the stable row.
/// </summary>
public abstract partial class ActivityItemPresentationRow : ChatPresentationRow
{
    public abstract object Source { get; }

    public abstract DateTimeOffset CreatedAt { get; }

    public abstract DateTimeOffset? FinishedAt { get; }

    public abstract bool IsRunning { get; }

    /// <summary>Gets whether the activity is blocked on explicit user interaction.</summary>
    public virtual bool IsWaitingForUserInput => false;

    [ObservableProperty] public partial bool IsExpanded { get; set; }
}

/// <summary>Projects one reasoning span as a compact, independently expandable activity.</summary>
/// <remarks>
/// A missing span completion time does not prove that work is still running after restart.
/// The owning assistant supplies runtime liveness; the Group independently tracks waits
/// between model calls. Templates observe both sources directly.
/// </remarks>
public sealed class ReasoningActivityItemPresentationRow(
    AssistantChatMessage assistant,
    AssistantChatMessageReasoningSpan reasoning,
    IDynamicLocaleKey headerKey
) : ActivityItemPresentationRow
{
    public AssistantChatMessage Assistant { get; } = assistant;

    public AssistantChatMessageReasoningSpan ReasoningSpan { get; } = reasoning;

    public IDynamicLocaleKey HeaderKey { get; } = headerKey;

    public override object Source => ReasoningSpan;

    public override DateTimeOffset CreatedAt => ReasoningSpan.CreatedAt;

    public override DateTimeOffset? FinishedAt => ReasoningSpan.FinishedAt;

    public override bool IsRunning => Assistant.IsBusy && ReasoningSpan.FinishedAt is null;
}

/// <summary>Projects one function-call message while retaining its original display blocks.</summary>
public sealed class FunctionCallActivityItemPresentationRow(
    FunctionCallChatMessage functionCall,
    IDynamicLocaleKey fallbackHeader
) : ActivityItemPresentationRow
{
    public FunctionCallChatMessage FunctionCall { get; } = functionCall;

    public IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks => FunctionCall.DisplayBlocks;

    public IDynamicLocaleKey FallbackHeaderKey { get; } = fallbackHeader;

    public override object Source => FunctionCall;

    public override DateTimeOffset CreatedAt => FunctionCall.CreatedAt;

    public override DateTimeOffset? FinishedAt => FunctionCall.IsBusy ? null : FunctionCall.FinishedAt;

    public override bool IsRunning => FunctionCall.IsBusy;

    public override bool IsWaitingForUserInput => FunctionCall.IsWaitingForUserInput;
}

/// <summary>
/// Owns one non-persisted operation and its stable position in the activity chronology.
/// Workers access the operation through <see cref="IBusyActivity"/>. Completion ends the
/// operation, not the retained row or its delayed Group transition.
/// </summary>
public sealed class BusyActivityItemPresentationRow : ActivityItemPresentationRow, IBusyActivity
{
    /// <inheritdoc />
    public LucideIconKind Icon
    {
        get => _icon;
        set
        {
            if (!IsRunning) return;
            SetProperty(ref _icon, value);
        }
    }

    /// <inheritdoc />
    public IDynamicLocaleKey HeaderKey
    {
        get => _headerKey;
        set
        {
            if (!IsRunning) return;
            SetProperty(ref _headerKey, value);
        }
    }

    /// <inheritdoc />
    public IDynamicLocaleKey? SecondaryHeaderKey
    {
        get => _secondaryHeaderKey;
        set
        {
            if (!IsRunning) return;
            SetProperty(ref _secondaryHeaderKey, value);
        }
    }

    /// <inheritdoc />
    public override object Source => this;

    /// <inheritdoc />
    public override DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public override DateTimeOffset? FinishedAt
    {
        get
        {
            var ticks = Interlocked.Read(ref _finishedAtUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <inheritdoc />
    public override bool IsRunning => FinishedAt is null;

    /// <summary>Gets the owning node, or null when the operation could not be attached.</summary>
    internal ChatMessageNode? OwnerNode { get; }

    /// <summary>Gets the last persisted span present when the operation began, used only as a placement anchor.</summary>
    internal AssistantChatMessageSpan? AnchorSpan { get; }

    internal bool RemoveAfterCompletion { get; }

    private LucideIconKind _icon;
    private IDynamicLocaleKey _headerKey;
    private IDynamicLocaleKey? _secondaryHeaderKey;
    private long _finishedAtUtcTicks;
    private Action<BusyActivityItemPresentationRow>? _onCompleted;

    /// <summary>
    /// Creates an operation with fixed placement and retention. A null owner represents an
    /// unattached operation; completion cleanup is independent of a materialized turn.
    /// </summary>
    internal BusyActivityItemPresentationRow(
        LucideIconKind icon,
        IDynamicLocaleKey headerKey,
        IDynamicLocaleKey? secondaryHeaderKey,
        ChatMessageNode? ownerNode,
        AssistantChatMessageSpan? anchorSpan,
        bool removeAfterCompletion,
        Action<BusyActivityItemPresentationRow>? onCompleted)
    {
        _icon = icon;
        _headerKey = headerKey;
        _secondaryHeaderKey = secondaryHeaderKey;
        OwnerNode = ownerNode;
        AnchorSpan = anchorSpan;
        RemoveAfterCompletion = removeAfterCompletion;
        _onCompleted = onCompleted;
    }

    /// <summary>Completes the operation once; later updates cannot restart it.</summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _finishedAtUtcTicks, DateTimeOffset.UtcNow.UtcDateTime.Ticks, 0) != 0) return;
        // Release the storage callback before notifying materialized turns. The callback marshals
        // membership cleanup to the dispatcher even when no turn currently observes this row.
        Interlocked.Exchange(ref _onCompleted, null)?.Invoke(this);
        OnPropertyChanged(nameof(FinishedAt));
        OnPropertyChanged(nameof(IsRunning));
    }

    /// <summary>Releases the context reference without completing work still owned by a caller.</summary>
    internal void DetachCompletion() => Interlocked.Exchange(ref _onCompleted, null);
}

/// <summary>
/// Represents one contiguous process segment. Its items remain stable while the header tracks the
/// latest item, allowing the running title to change without replacing the group row.
///
/// <para>
/// The item list is deliberately owned by the group instead of being flattened into the outer chat
/// list. The outer <c>VariableHeightVirtualizingStackPanel</c> therefore measures one Group row,
/// while this row owns a bounded, explicitly expanded timeline. Updating the list uses reference
/// identity so existing activity presenters remain stable during streaming and branch updates.
/// </para>
/// </summary>
public sealed class ActivityGroupPresentationRow : ChatPresentationRow
{
    /// <summary>
    /// Gets the live, read-only activity list consumed by the Group timeline. The projection edits
    /// the private <see cref="BindableList{T}"/> in place; consumers never receive a mutable source.
    /// </summary>
    public IReadOnlyBindableList<ActivityItemPresentationRow> Items => _items;

    public ActivityItemPresentationRow LatestItem => Items[^1];

    public DateTimeOffset CreatedAt => Items.Min(item => item.CreatedAt);

    /// <summary>
    /// Gets the effective end of this process segment. A trailing Group can remain active after its
    /// latest item has completed while the assistant waits for the model's continuation. Capturing
    /// the end of that presentation interval prevents the elapsed time from jumping backwards to
    /// the earlier tool completion time when the continuation finally arrives.
    /// </summary>
    public DateTimeOffset? FinishedAt
    {
        get
        {
            if (IsRunning) return null;

            var finishedAt = _presentationFinishedAt;
            foreach (var item in Items)
            {
                if (item.FinishedAt is not { } itemFinishedAt ||
                    finishedAt is { } currentFinishedAt && currentFinishedAt >= itemFinishedAt)
                    continue;

                finishedAt = itemFinishedAt;
            }

            return finishedAt;
        }
    }

    /// <summary>
    /// Gets whether this is the current active process segment. Item state remains authoritative
    /// for every activity in the segment, including parallel calls that are not the latest item;
    /// <see cref="_isAwaitingContinuation"/> only keeps the outer Group active during the real gap
    /// between a completed tool and the model's next output.
    /// </summary>
    public bool IsRunning => Items.AsValueEnumerable().Any(item => item.IsRunning) || _isAwaitingContinuation;

    /// <summary>
    /// Gets whether at least one active child activity is blocked on explicit user interaction.
    /// Continuation waits do not qualify: they keep the Group alive while the model responds, but
    /// do not ask the user to take action.
    /// </summary>
    public bool IsWaitingForUserInput =>
        IsRunning && Items.AsValueEnumerable().Any(item => item.IsWaitingForUserInput);

    /// <summary>
    /// Gets the local statistics snapshot for this contiguous activity segment.
    /// </summary>
    public ChatActivityStatistics Statistics => _statistics;

    /// <summary>
    /// Gets or sets whether the timeline is currently visible. This is presentation-only state and
    /// does not affect the outer projection because the timeline lives inside this row.
    /// </summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (!value) ResetItemExpansion();
        }
    }

    private readonly BindableList<ActivityItemPresentationRow> _items = [];

    private bool _hasReceivedItems;
    private bool _lastKnownIsRunning;
    private bool _isAwaitingContinuation;
    private bool _isExpanded;
    private DateTimeOffset? _presentationFinishedAt;
    private ChatActivityStatistics _statistics;

    /// <summary>
    /// Applies expansion required by the projection without recording it as a user gesture. Group
    /// expansion no longer changes the outer row list, so this method only updates local bindings.
    /// </summary>
    public void SetExpandedFromPresentation(bool value) =>
        SetExpandedFromPresentationCore(value);

    /// <summary>
    /// Reconciles the current timeline items and its turn-local continuation state by reference,
    /// then reports whether an already-running Group just completed. Both inputs are applied before
    /// transition detection so completion of the latest item cannot briefly complete the Group
    /// while the same assistant invocation is still waiting for more model output.
    ///
    /// <para>
    /// The first snapshot is initialized directly: historical completed Groups start collapsed,
    /// while newly observed active Groups start expanded without a close animation.
    /// </para>
    /// </summary>
    /// <param name="items">The stable activity rows belonging to this contiguous segment.</param>
    /// <param name="isAwaitingContinuation">
    /// Whether this is the trailing process segment of an assistant invocation that is still busy.
    /// This affects only the Group; it never changes the truthful running state of an activity item.
    /// </param>
    public bool UpdateItems(IReadOnlyList<ActivityItemPresentationRow> items, bool isAwaitingContinuation)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("An activity group cannot be empty.", nameof(items));

        // Activity rows read their running state directly from the source FunctionCall/Reasoning
        // object. Keep the last projected value separately so a source property change can still
        // be recognized as a running-to-completed transition before the refreshed row is rendered.
        var wasRunning = _hasReceivedItems && _lastKnownIsRunning;

        ReconcileItems(items);
        _isAwaitingContinuation = isAwaitingContinuation;

        var isRunning = IsRunning;
        // Running Groups are always expanded. Their header is non-interactive in the running style,
        // so no extra "user collapsed" or fallback state is necessary. Completion deliberately
        // leaves the current value untouched until the projection's short placement delay expires.
        if (isRunning)
        {
            _presentationFinishedAt = null;
            SetExpandedFromPresentationCore(true);
        }
        else if (wasRunning)
        {
            // The segment includes a genuine wait for the next model response even though no child
            // item owns that interval. Preserve its visual endpoint entirely in the presentation
            // row; persisted activity timestamps remain unchanged.
            _presentationFinishedAt = DateTimeOffset.UtcNow;
        }

        _hasReceivedItems = true;
        _lastKnownIsRunning = isRunning;
        Refresh();
        return wasRunning && !isRunning;
    }

    private void Refresh()
    {
        // Items is a stable bindable list. Its own collection notifications describe membership
        // changes; raising PropertyChanged for the same list would encourage an ItemsControl to
        // rebind and needlessly recreate the local timeline.
        OnPropertyChanged(nameof(LatestItem));
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(FinishedAt));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsWaitingForUserInput));
        SetProperty(ref _statistics, ChatActivityStatistics.Calculate(Items), nameof(Statistics));
    }

    private void SetExpandedFromPresentationCore(bool value)
    {
        if (!SetProperty(ref _isExpanded, value, nameof(IsExpanded))) return;
        if (!value) ResetItemExpansion();
    }

    private void ResetItemExpansion()
    {
        // The detail VisualTree is destroyed by ConditionalContentControl when this Group closes.
        // Resetting the child flags keeps the next opening lightweight and avoids resurrecting a
        // large reasoning/terminal view merely because the parent card was reopened.
        foreach (var item in _items)
            item.IsExpanded = false;
    }

    private void ReconcileItems(IReadOnlyList<ActivityItemPresentationRow> desired)
    {
        var prefix = 0;
        while (prefix < _items.Count && prefix < desired.Count && ReferenceEquals(_items[prefix], desired[prefix]))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < _items.Count - prefix && suffix < desired.Count - prefix &&
               ReferenceEquals(_items[_items.Count - 1 - suffix], desired[desired.Count - 1 - suffix]))
        {
            suffix++;
        }

        // Remove from the end of the changed range so indices stay valid and each notification is
        // local to the Group timeline. The common prefix/suffix keeps unaffected item controls and
        // their animations alive.
        for (var i = _items.Count - prefix - suffix - 1; i >= 0; i--)
            _items.RemoveAt(prefix + i);

        var insertCount = desired.Count - prefix - suffix;
        for (var i = 0; i < insertCount; i++)
            _items.Insert(prefix + i, desired[prefix + i]);
    }
}

/// <summary>Displays a formal text or image span in its original chronological position.</summary>
public abstract partial class AssistantOutputPresentationRow(ChatMessageNode assistantNode) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    [ObservableProperty] public partial bool IsFinal { get; set; }
}

public sealed class AssistantTextOutputPresentationRow(
    ChatMessageNode assistantNode,
    AssistantChatMessageTextSpan span
) : AssistantOutputPresentationRow(assistantNode)
{
    public AssistantChatMessageTextSpan TextSpan { get; } = span;

    /// <summary>
    /// Gets or sets the latest Markdown state committed by a renderer or prepared before the row is
    /// presented. Keeping the most recent state avoids clearing the visual tree while a newer
    /// source version is still being parsed.
    /// </summary>
    public MarkdownDocumentUpdate? CachedDocumentUpdate
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            // Streaming updates do not change which source the renderer should bind. Completion
            // raises this notification once, while a final update that arrives after completion
            // raises it here and detaches the builder.
            if (!CanReceiveUpdates) OnPropertyChanged(nameof(RenderingMarkdownBuilder));
        }
    }

    /// <summary>
    /// Gets the streaming source while this span can still change or its final document has not
    /// caught up. Once the source is sealed, the matching cached update becomes authoritative.
    /// </summary>
    public ObservableStringBuilder? RenderingMarkdownBuilder
    {
        get
        {
            var builder = TextSpan.ContentMarkdownBuilder;
            return CanReceiveUpdates || CachedDocumentUpdate?.Version != builder.Version ? builder : null;
        }
    }

    internal bool CanReceiveUpdates { get; private set; }

    /// <summary>
    /// Updates the source lifetime declared by the chat pipeline. Version equality synchronizes
    /// parsing completion; it never infers whether the source may receive more content.
    /// </summary>
    internal void UpdateCanReceiveUpdates(bool value)
    {
        if (CanReceiveUpdates == value) return;

        CanReceiveUpdates = value;
        OnPropertyChanged(nameof(RenderingMarkdownBuilder));
    }
}

public sealed class AssistantImageOutputPresentationRow(
    ChatMessageNode assistantNode,
    AssistantChatMessageImageSpan span
) : AssistantOutputPresentationRow(assistantNode)
{
    public AssistantChatMessageImageSpan ImageSpan { get; } = span;
}

/// <summary>Displays an assistant error, distinguishing process history from terminal failure.</summary>
public sealed class AssistantErrorPresentationRow(ChatMessageNode assistantNode, bool isTerminal) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    public bool IsTerminal { get; } = isTerminal;

    public AssistantChatMessage Assistant => (AssistantChatMessage)AssistantNode.Message;
}

/// <summary>Projects a persisted stopped outcome independently of runtime diagnostics.</summary>
public sealed class AssistantCanceledPresentationRow(ChatMessageNode assistantNode, bool isTerminal) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    public bool IsTerminal { get; } = isTerminal;
}

/// <summary>Provides an explicit result for a successful turn without formal output.</summary>
public sealed class NoResponsePresentationRow : ChatPresentationRow;

/// <summary>
/// Hosts statistics and the existing retry/copy operation surface for the latest invocation.
/// </summary>
public sealed class TurnFooterPresentationRow : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; }
    public AssistantChatMessage AssistantMessage => (AssistantChatMessage)AssistantNode.Message;

    public TurnFooterPresentationRow(ChatMessageNode assistantNode)
    {
        Debug.Assert(assistantNode.Message is AssistantChatMessage, "TurnFooterPresentationRow must be constructed with an assistant message node.");
        AssistantNode = assistantNode;
    }
}

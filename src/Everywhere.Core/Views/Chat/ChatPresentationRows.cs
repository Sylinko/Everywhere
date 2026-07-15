using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Lucide.Avalonia;

namespace Everywhere.Views;

/// <summary>
/// Centralizes the two small structural constants shared by whole-turn and local activity
/// presentation. They deliberately describe presentation policy only and never affect persisted
/// chat messages or their spans.
/// </summary>
internal static class ChatActivityPresentationPolicy
{
    /// <summary>
    /// Completed activity collections at or below this size are shown as direct activity rows.
    /// Larger collections keep their aggregate container to avoid overwhelming the conversation.
    /// </summary>
    public const int InlineActivityLimit = 2;

    /// <summary>
    /// Leaves a just-completed running Group in place long enough for the 320 ms glow transition
    /// to finish before the row is collapsed or replaced by its direct activity rows.
    /// </summary>
    public static readonly TimeSpan CompletionPlacementDelay = TimeSpan.FromMilliseconds(400);
}

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
/// Base for stable activity rows. The owning turn invokes <see cref="Refresh"/> when a source value
/// changes. Expansion is entirely local to the activity presenter: it creates or destroys detail
/// content inside the current visual container and therefore never asks the outer chat projection
/// to rebuild its virtualized row list.
/// </summary>
public abstract partial class ActivityItemPresentationRow : ChatPresentationRow
{
    public abstract object Source { get; }
    public abstract LucideIconKind Icon { get; }
    public abstract IDynamicLocaleKey HeaderKey { get; }
    public abstract DateTimeOffset CreatedAt { get; }
    public abstract DateTimeOffset? FinishedAt { get; }
    public abstract bool IsRunning { get; }
    public abstract string? PreviewText { get; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }

    /// <summary>
    /// Raises changes for the source-backed properties shared by every activity kind. Derived rows
    /// extend this method only for properties supplied by their own source type.
    /// </summary>
    public virtual void Refresh()
    {
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HeaderKey));
        OnPropertyChanged(nameof(FinishedAt));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(PreviewText));
    }
}

/// <summary>Projects one reasoning span as a compact, independently expandable activity.</summary>
public sealed class ReasoningActivityItemPresentationRow(
    AssistantChatMessageReasoningSpan reasoning,
    IDynamicLocaleKey headerKey
) : ActivityItemPresentationRow
{
    public override object Source => reasoning;
    public override LucideIconKind Icon => LucideIconKind.Brain;
    public override IDynamicLocaleKey HeaderKey => headerKey;
    public override DateTimeOffset CreatedAt => reasoning.CreatedAt;
    public override DateTimeOffset? FinishedAt => reasoning.FinishedAt;
    public override bool IsRunning => reasoning.FinishedAt is null;
    public override string? PreviewText => reasoning.ReasoningOutput;
    public AssistantChatMessageReasoningSpan ReasoningSpan => reasoning;
}

/// <summary>Projects one function-call message while retaining its original display blocks.</summary>
public sealed class FunctionCallActivityItemPresentationRow(
    FunctionCallChatMessage functionCall,
    IDynamicLocaleKey fallbackHeader
) : ActivityItemPresentationRow
{
    public override object Source => functionCall;
    public override LucideIconKind Icon => functionCall.Icon;
    public override IDynamicLocaleKey HeaderKey => functionCall.HeaderKey ?? fallbackHeader;
    public override DateTimeOffset CreatedAt => functionCall.CreatedAt;
    public override DateTimeOffset? FinishedAt => functionCall.IsBusy ? null : functionCall.FinishedAt;
    public override bool IsRunning => functionCall.IsBusy;
    public override string? PreviewText => functionCall.Content;
    public IDynamicLocaleKey? ErrorMessageKey => functionCall.ErrorMessageKey;
    public int CallCount => functionCall.Calls.Count;
    public ChatPluginActivityPreview? ActivityPreview => functionCall.ActivityPreview;
    public bool HasPreview => ActivityPreview is not null || !string.IsNullOrEmpty(PreviewText);
    public IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks => functionCall.DisplayBlocks;

    /// <inheritdoc/>
    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(nameof(ErrorMessageKey));
        OnPropertyChanged(nameof(CallCount));
        OnPropertyChanged(nameof(ActivityPreview));
        OnPropertyChanged(nameof(HasPreview));
        // DisplayBlocks is the same source-backed bindable list for the lifetime of the function
        // call. Its own collection notifications update the lazy detail ItemsControl; raising
        // PropertyChanged here would make that control rebind the identical list unnecessarily.
    }
}

/// <summary>
/// Represents a short-lived, non-persisted operation reported directly by <see cref="ChatContext"/>.
/// It intentionally exposes no expandable detail surface: its sole purpose is to let transient
/// states such as MCP startup or tool-argument generation participate in the same running Group,
/// iconography, elapsed-time display, and glow transition as persisted activities.
/// </summary>
public sealed class BusyActivityItemPresentationRow(
    LucideIconKind icon,
    IDynamicLocaleKey headerKey,
    DateTimeOffset createdAt
) : ActivityItemPresentationRow
{
    private DateTimeOffset? _finishedAt;

    public override object Source => this;
    public override LucideIconKind Icon => icon;
    public override IDynamicLocaleKey HeaderKey => headerKey;
    public override DateTimeOffset CreatedAt => createdAt;
    public override DateTimeOffset? FinishedAt => _finishedAt;
    public override bool IsRunning => _finishedAt is null;
    public override string? PreviewText => null;

    /// <summary>
    /// Gets the assistant node and the last persisted span that existed when the temporary activity
    /// began. The projection uses this pair only to place the row chronologically; neither value is
    /// serialized or copied into the message graph.
    /// </summary>
    internal ChatMessageNode? AssistantNode { get; set; }

    internal AssistantChatMessageSpan? AnchorSpan { get; set; }

    /// <summary>Completes the transient activity while preserving the stable row instance.</summary>
    internal void Complete(DateTimeOffset finishedAt)
    {
        if (_finishedAt is not null) return;
        _finishedAt = finishedAt;
        Refresh();
    }
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
    /// for the work represented by that item; <see cref="_isAwaitingContinuation"/> only keeps the
    /// outer Group active during the real gap between a completed tool and the model's next output.
    /// </summary>
    public bool IsRunning => LatestItem.IsRunning || _isAwaitingContinuation;

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
public sealed partial class AssistantOutputPresentationRow(ChatMessageNode assistantNode, AssistantChatMessageSpan span) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;
    public AssistantChatMessageSpan Span { get; } = span;
    [ObservableProperty] public partial bool IsFinal { get; set; }
}

/// <summary>Displays an assistant error, distinguishing process history from terminal failure.</summary>
public sealed class AssistantErrorPresentationRow(ChatMessageNode assistantNode, bool isTerminal) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;
    public bool IsTerminal { get; } = isTerminal;
    public AssistantChatMessage Assistant => (AssistantChatMessage)AssistantNode.Message;
}

/// <summary>Provides an explicit result for a successful turn without formal output.</summary>
public sealed class NoResponsePresentationRow : ChatPresentationRow;

/// <summary>Hosts statistics and the existing retry/copy operation surface for the latest invocation.</summary>
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
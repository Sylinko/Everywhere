using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
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
/// Displays a non-assistant message through captured values and a stable command identity.
/// </summary>
public class ChatMessagePresentationRow(ChatContext context, ChatMessageNode node) : ChatPresentationRow
{
    public ChatMessageNode Node { get; } = node;

    public ChatMessageState? State => _state;

    public ChatBranchSelection Branch { get; } = new(context, node);

    private ChatMessageState? _state;

    /// <summary>
    /// Captures display values and branch choices without replacing the row. Call before first publication.
    /// </summary>
    public void Refresh()
    {
        SetProperty(ref _state, Node.Message.Capture(), nameof(State));
        Branch.Refresh();
    }
}

/// <summary>
/// Represents the lightweight skeleton shown before a busy assistant emits a span.
/// </summary>
public sealed class PendingAssistantPresentationRow : ChatPresentationRow;

/// <summary>
/// Owns the completed-turn process expansion state and its aggregate activity statistics.
/// </summary>
public sealed partial class ProcessSummaryPresentationRow : ChatPresentationRow
{
    /// <summary>
    /// Gets the latest whole-turn statistics snapshot. Keeping the counters under one property makes
    /// the binding surface stable when new activity metrics are introduced.
    /// </summary>
    public ChatActivityStatistics Statistics => _statistics;

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    private ChatActivityStatistics _statistics;

    /// <summary>
    /// Replaces the aggregate snapshot when the turn projection changes.
    /// </summary>
    public void UpdateStatistics(ChatActivityStatistics statistics)
    {
        SetProperty(ref _statistics, statistics, nameof(Statistics));
    }
}

/// <summary>
/// Base for stable activity rows. Templates consume captured values; the source supplies identity
/// and commands only. Expansion is presentation-only and belongs to the stable row.
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

    /// <summary>
    /// Captures current activity values without changing identity or user expansion state.
    /// </summary>
    public abstract void Refresh();
}

/// <summary>Projects one reasoning span as a compact, independently expandable activity.</summary>
/// <remarks>
/// A missing span completion time does not prove that work is still running after restart.
/// The owning assistant supplies runtime liveness; the Group independently tracks waits
/// between model calls. Both values are captured in the same render pass.
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

    public ChatReasoningState State => _state;

    public override DateTimeOffset CreatedAt => State.CreatedAt;

    public override DateTimeOffset? FinishedAt => State.FinishedAt;

    public override bool IsRunning => State.IsRunning;

    private ChatReasoningState _state = ChatReasoningState.Capture(assistant, reasoning, null);

    /// <summary>
    /// Captures current values, reusing text when an unrelated turn change invalidates the row.
    /// </summary>
    public override void Refresh()
    {
        SetProperty(ref _state, ChatReasoningState.Capture(Assistant, ReasoningSpan, _state), nameof(State));
    }
}

/// <summary>Projects one function-call message while retaining its original display blocks.</summary>
public sealed class FunctionCallActivityItemPresentationRow(
    FunctionCallChatMessage functionCall,
    IDynamicLocaleKey fallbackHeader
) : ActivityItemPresentationRow
{
    public FunctionCallChatMessage FunctionCall { get; } = functionCall;

    public IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks => State.DisplayBlocks;

    public IDynamicLocaleKey FallbackHeaderKey { get; } = fallbackHeader;

    public override object Source => FunctionCall;

    public ChatFunctionState State => _state;

    public override DateTimeOffset CreatedAt => State.CreatedAt;

    public override DateTimeOffset? FinishedAt => State.FinishedAt;

    public override bool IsRunning => State.IsRunning;

    public override bool IsWaitingForUserInput => State.IsWaitingForUserInput;

    private ChatFunctionState _state = functionCall.Capture();

    /// <summary>
    /// Replaces captured function values without replacing this row or its expansion state.
    /// </summary>
    public override void Refresh()
    {
        SetProperty(ref _state, FunctionCall.Capture(), nameof(State));
    }
}

/// <summary>
/// Captures a turn-owned runtime activity while keeping expansion and visual identity in the view.
/// </summary>
public sealed class BusyActivityItemPresentationRow(ChatBusyActivity activity) : ActivityItemPresentationRow
{
    /// <summary>
    /// Gets the model operation represented by this row.
    /// </summary>
    public ChatBusyActivity Activity { get; } = activity;

    public LucideIconKind Icon => _state.Icon;

    public IDynamicLocaleKey HeaderKey => _state.HeaderKey;

    public IDynamicLocaleKey? SecondaryHeaderKey => _state.SecondaryHeaderKey;

    public override object Source => Activity;

    public override DateTimeOffset CreatedAt => Activity.CreatedAt;

    public override DateTimeOffset? FinishedAt => _state.FinishedAt;

    public override bool IsRunning => FinishedAt is null;

    internal ChatMessageNode? Owner => Activity.Owner;

    internal AssistantChatMessageSpan? AnchorSpan => Activity.AnchorSpan;

    private ChatBusyActivityState _state = activity.State;

    /// <inheritdoc />
    public override void Refresh()
    {
        var state = Activity.State;
        if (state == _state)
        {
            return;
        }

        _state = state;
        OnPropertyChanged(nameof(Icon));
        OnPropertyChanged(nameof(HeaderKey));
        OnPropertyChanged(nameof(SecondaryHeaderKey));
        OnPropertyChanged(nameof(FinishedAt));
        OnPropertyChanged(nameof(IsRunning));
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
                if (item.FinishedAt is not { } itemFinishedAt || finishedAt is { } currentFinishedAt && currentFinishedAt >= itemFinishedAt)
                {
                    continue;
                }

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
    public bool IsWaitingForUserInput => IsRunning && Items.AsValueEnumerable().Any(item => item.IsWaitingForUserInput);

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
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;
            if (!value) ResetItemExpansion();
        }
    }

    private readonly BindableList<ActivityItemPresentationRow> _items = [];

    private bool _hasReceivedItems;
    private bool _lastKnownIsRunning;
    private bool _isAwaitingContinuation;
    private DateTimeOffset? _presentationFinishedAt;
    private ChatActivityStatistics _statistics;

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

        // Child snapshots have already refreshed. Retain the previous group state so completion
        // is detected before applying the new membership and continuation state together.
        var wasRunning = _hasReceivedItems && _lastKnownIsRunning;

        ReconcileItems(items);
        _isAwaitingContinuation = isAwaitingContinuation;

        var isRunning = IsRunning;
        // Expansion defaults are applied once. Subsequent progress, membership changes and
        // completion must not overwrite the user's choice on this stable group.
        if (!_hasReceivedItems)
        {
            IsExpanded = isRunning;
        }

        if (isRunning)
        {
            _presentationFinishedAt = null;
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
        {
            _items.RemoveAt(prefix + i);
        }

        var insertCount = desired.Count - prefix - suffix;
        for (var i = 0; i < insertCount; i++)
        {
            _items.Insert(prefix + i, desired[prefix + i]);
        }
    }
}

/// <summary>Displays a formal text or image span in its original chronological position.</summary>
public abstract partial class AssistantOutputPresentationRow(ChatMessageNode assistantNode) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    [ObservableProperty] public partial bool IsFinal { get; set; }
}

/// <summary>
/// Owns the parsed document for one stable text span across control recycling.
/// </summary>
public sealed class AssistantTextOutputPresentationRow(
    ChatMessageNode assistantNode,
    AssistantChatMessageTextSpan span
) : AssistantOutputPresentationRow(assistantNode), IDisposable
{
    public AssistantChatMessageTextSpan TextSpan { get; } = span;

    /// <summary>
    /// Gets the view-owned Markdown cache. Templates bind the document, never the mutable buffer.
    /// </summary>
    public ChatMarkdownPresentation Markdown { get; } = new(span.ContentMarkdownBuilder);

    /// <inheritdoc />
    public void Dispose() => Markdown.Dispose();
}

public sealed class AssistantImageOutputPresentationRow(
    ChatMessageNode assistantNode,
    AssistantChatMessageImageSpan span
) : AssistantOutputPresentationRow(assistantNode)
{
    public AssistantChatMessageImageSpan ImageSpan { get; } = span;

    public FileAttachment? ImageOutput => _imageOutput;

    private FileAttachment? _imageOutput = span.ImageOutput;

    internal void Refresh() => SetProperty(ref _imageOutput, ImageSpan.ImageOutput, nameof(ImageOutput));
}

/// <summary>Displays an assistant error and the current continuation affordance.</summary>
public sealed partial class AssistantErrorPresentationRow(ChatMessageNode assistantNode) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    [ObservableProperty]
    public partial bool IsContinuationTarget { get; internal set; }

    public ChatAssistantState State => _state;

    private ChatAssistantState _state = ((AssistantChatMessage)assistantNode.Message).Capture();

    internal void Refresh()
    {
        SetProperty(ref _state, ((AssistantChatMessage)AssistantNode.Message).Capture(), nameof(State));
    }
}

/// <summary>Projects a persisted stopped outcome independently of runtime diagnostics.</summary>
public sealed partial class AssistantCanceledPresentationRow(ChatMessageNode assistantNode) : ChatPresentationRow
{
    public ChatMessageNode AssistantNode { get; } = assistantNode;

    [ObservableProperty]
    public partial bool IsContinuationTarget { get; internal set; }
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

    public ChatAssistantState State => _state;

    private ChatAssistantState _state;

    public ChatBranchSelection Branch { get; }

    internal void Refresh()
    {
        SetProperty(ref _state, AssistantMessage.Capture(), nameof(State));
        Branch.Refresh();
    }

    public TurnFooterPresentationRow(ChatContext context, ChatMessageNode assistantNode)
    {
        Debug.Assert(assistantNode.Message is AssistantChatMessage, "TurnFooterPresentationRow must be constructed with an assistant message node.");
        AssistantNode = assistantNode;
        Branch = new ChatBranchSelection(context, assistantNode);
        _state = AssistantMessage.Capture();
    }
}
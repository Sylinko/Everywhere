using System.Globalization;
using Everywhere.Chat;
using Everywhere.Common;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Views;

/// <summary>
/// Keeps a lightweight, UI-thread-owned index of user turns in the selected branch, independent
/// of the materialized message window. Entries retain identity across incremental branch edits.
/// </summary>
public sealed class ChatTurnNavigationIndex : IDisposable
{
    /// <summary>
    /// Gets the ordered user turns, including user actions.
    /// </summary>
    public IReadOnlyList<Entry> Turns => _turns;

    /// <summary>
    /// Gets the turn cards and date headers in display order.
    /// </summary>
    public IReadOnlyList<PreviewItem> PreviewItems => _previewItems;

    /// <summary>
    /// Raised after a branch change has been applied on the UI thread.
    /// </summary>
    public event Action? Changed;

    private readonly List<ChatMessageNode> _nodes = [];
    private readonly List<Entry> _turns = [];
    private readonly List<PreviewItem> _previewItems = [];
    private readonly IDisposable _subscription;

    /// <summary>
    /// Observes the visible selected branch of the supplied context.
    /// </summary>
    public ChatTurnNavigationIndex(ChatContext context)
    {
        _subscription = context
            .ConnectDisplayItems()
            .ObserveOnAvaloniaDispatcher()
            .Clone(_nodes)
            .Subscribe(_ =>
            {
                Reconcile();
                Changed?.Invoke();
            });
    }

    private void Reconcile()
    {
        // Only structural branch notifications visit the full index; rendering and preview reads
        // never scan history. Reuse entries by node identity when edits replace a branch suffix.
        var existing = _turns.AsValueEnumerable().ToDictionary(turn => turn.Node);
        var position = 0;
        for (var i = 0; i < _nodes.Count; i++)
        {
            var node = _nodes[i];
            if (node.Message.Role != AuthorRole.User) continue;

            if (position > 0) _turns[position - 1].End = i;
            if (!existing.TryGetValue(node, out var entry)) entry = new Entry(this, node);
            entry.Start = i;
            entry.End = _nodes.Count;
            if (position < _turns.Count) _turns[position] = entry;
            else _turns.Add(entry);
            position++;
        }
        if (position < _turns.Count) _turns.RemoveRange(position, _turns.Count - position);

        ReconcilePreviewItems();
    }

    private void ReconcilePreviewItems()
    {
        var existingHeaders = _previewItems.AsValueEnumerable().OfType<DateHeader>().ToDictionary(header => header.FirstTurn);
        _previewItems.Clear();

        DateOnly? previousDate = null;
        for (var i = 0; i < _turns.Count; i++)
        {
            var turn = _turns[i];
            var date = DateOnly.FromDateTime(turn.Node.Message.CreatedAt.ToLocalTime().Date);
            if (date != previousDate)
            {
                if (!existingHeaders.TryGetValue(turn, out var header) || header.Date != date)
                {
                    header = new DateHeader(turn, date);
                }

                header.TurnIndex = i;
                header.PreviewIndex = _previewItems.Count;
                _previewItems.Add(header);
            }

            turn.TurnIndex = i;
            turn.PreviewIndex = _previewItems.Count;
            _previewItems.Add(turn);
            previousDate = date;
        }
    }

    /// <summary>
    /// Enumerates only the messages belonging to this indexed turn.
    /// </summary>
    public IEnumerable<ChatMessage> GetMessages(Entry entry)
    {
        for (var i = entry.Start; i < entry.End; i++) yield return _nodes[i].Message;
    }

    /// <inheritdoc />
    public void Dispose() => _subscription.Dispose();

    /// <summary>
    /// A measured item in the polymorphic turn-preview sequence.
    /// </summary>
    public abstract class PreviewItem(ChatTurnNavigationIndex index)
    {
        /// <summary>
        /// Gets the index that owns this item.
        /// </summary>
        public ChatTurnNavigationIndex Index { get; } = index;

        /// <summary>
        /// Gets or sets the index of this item in the preview sequence.
        /// </summary>
        public int PreviewIndex { get; set; }

        /// <summary>
        /// Gets or sets the index of this item in the turn sequence.
        /// </summary>
        public int TurnIndex { get; set; }
    }

    /// <summary>
    /// A stable user-node identity and its current range in the visible branch.
    /// </summary>
    public sealed class Entry(ChatTurnNavigationIndex index, ChatMessageNode node) : PreviewItem(index)
    {
        /// <summary>
        /// Gets the user message to reveal when the turn is activated.
        /// </summary>
        public ChatMessageNode Node { get; } = node;

        /// <summary>
        /// Gets or sets the first index of the node in the visible branch.
        /// </summary>
        public int Start { get; set; }

        /// <summary>
        /// Gets or sets the first index after the node in the visible branch.
        /// </summary>
        public int End { get; set; }
    }

    /// <summary>
    /// A date heading immediately preceding the first turn from that local calendar day.
    /// </summary>
    public sealed class DateHeader(Entry firstTurn, DateOnly date) : PreviewItem(firstTurn.Index)
    {
        /// <summary>
        /// Gets the local calendar date represented by this heading.
        /// </summary>
        public DateOnly Date { get; } = date;

        /// <summary>
        /// Gets the localized short month and day text.
        /// </summary>
        public string Text => Date.ToString(Abstractions.I18N.LocaleResolver.Common_ShortMonthDayPattern, CultureInfo.CurrentUICulture);

        /// <summary>
        /// Gets the first turn in the date.
        /// </summary>
        public Entry FirstTurn { get; } = firstTurn;
    }
}
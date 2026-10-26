using System.Globalization;
using Avalonia.Threading;
using Everywhere.Chat;

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
    /// Gets the runtime used to load a turn when its preview becomes visible.
    /// </summary>
    public ChatContext Context { get; }

    /// <summary>
    /// Raised after a branch change has been applied on the UI thread.
    /// </summary>
    public event Action? Changed;

    private readonly List<Entry> _turns = [];
    private readonly List<PreviewItem> _previewItems = [];
    private int _isRefreshQueued;
    private bool _isDisposed;

    /// <summary>
    /// Indexes native turn identities without materializing any message bodies.
    /// </summary>
    public ChatTurnNavigationIndex(ChatContext context)
    {
        Context = context;
        context.Data.BranchChanged += HandleBranchChanged;
        Reconcile();
    }

    private void HandleBranchChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _isRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _isRefreshQueued, 0);
            if (_isDisposed)
            {
                return;
            }

            Reconcile();
            Changed?.Invoke();
        });
    }

    private void Reconcile()
    {
        // Structural notifications visit only the turn index, never message payloads.
        var existing = _turns.ToDictionary(entry => entry.Turn);
        _turns.Clear();
        foreach (var turn in Context.Data.SelectedTurns)
        {
            _turns.Add(existing.TryGetValue(turn, out var entry) ? entry : new Entry(this, turn));
        }

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
            var date = DateOnly.FromDateTime(turn.Node.CreatedAt.ToLocalTime().Date);
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

    /// <inheritdoc />
    public void Dispose()
    {
        _isDisposed = true;
        Context.Data.BranchChanged -= HandleBranchChanged;
    }

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
    /// A stable native turn identity, independent of body loading and the materialized window.
    /// </summary>
    public sealed class Entry(ChatTurnNavigationIndex index, ChatTurn turn) : PreviewItem(index)
    {
        /// <summary>
        /// Gets the native turn represented by this entry.
        /// </summary>
        public ChatTurn Turn { get; } = turn;

        /// <summary>
        /// Gets the user node to reveal when the turn is activated.
        /// </summary>
        public ChatMessageNode Node => Turn.UserNode;
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
        public string Text => Date.ToString(Abstractions.I18N.LocaleKey.Common_ShortMonthDayPattern.I18N(), CultureInfo.CurrentUICulture);

        /// <summary>
        /// Gets the first turn in the date.
        /// </summary>
        public Entry FirstTurn { get; } = firstTurn;
    }
}
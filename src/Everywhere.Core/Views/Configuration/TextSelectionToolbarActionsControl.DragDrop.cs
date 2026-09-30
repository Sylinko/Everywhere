using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactions.DragAndDrop;
using Everywhere.StrategyEngine;

namespace Everywhere.Views;

public sealed partial class TextSelectionToolbarActionsControl
{
    public IDropHandler DropHandler => _dropHandler;

    private readonly ActionDropHandler _dropHandler = new(actions);

    /// <summary>
    /// Bridges the existing context drag/drop behaviors to a single persisted Move. The displayed
    /// collection is a read-only projection, so drag/drop must not remove and reinsert its rows.
    /// </summary>
    private sealed class ActionDropHandler(TextSelectionToolbarActions actions) : DropHandlerBase
    {
        private const double ScrollEdge = 32;
        private const double ScrollStep = 24;

        private ListBox? _listBox;
        private ScrollViewer? _scrollViewer;
        private DispatcherTimer? _scrollTimer;
        private Point _pointerPosition;
        private int _scrollDirection;
        private Item? _highlightedItem;

        public override bool Validate(object? sender, DragEventArgs e, object? sourceContext, object? targetContext, object? state) =>
            sender is ListBox && sourceContext is Item item && actions.Items.Contains(item.Action);

        public override void Enter(object? sender, DragEventArgs e, object? sourceContext, object? targetContext) =>
            Over(sender, e, sourceContext, targetContext);

        public override void Over(object? sender, DragEventArgs e, object? sourceContext, object? targetContext)
        {
            e.Handled = true;
            if (sender is not ListBox listBox || !Validate(sender, e, sourceContext, targetContext, null))
            {
                e.DragEffects = DragDropEffects.None;
                Clear();
                return;
            }

            e.DragEffects = DragDropEffects.Move;
            _listBox = listBox;
            _pointerPosition = e.GetPosition(listBox);
            UpdateIndicator(listBox, _pointerPosition);

            _scrollViewer = listBox.FindDescendantOfType<ScrollViewer>();
            if (_scrollViewer is not { } scrollViewer) return;

            var position = e.GetPosition(scrollViewer);
            _scrollDirection = position.Y < ScrollEdge ? -1 :
                position.Y > scrollViewer.Bounds.Height - ScrollEdge ? 1 : 0;

            if (_scrollDirection == 0)
            {
                _scrollTimer?.Stop();
                return;
            }

            if (_scrollTimer is null)
            {
                _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _scrollTimer.Tick += (_, _) => Scroll();
            }

            _scrollTimer.Start();
        }

        public override void Drop(object? sender, DragEventArgs e, object? sourceContext, object? targetContext)
        {
            e.Handled = true;
            e.DragEffects = DragDropEffects.None;
            try
            {
                if (sender is not ListBox listBox || sourceContext is not Item item) return;
                var sourceIndex = actions.Items.IndexOf(item.Action);
                if (sourceIndex < 0) return;

                var insertionIndex = FindInsertion(listBox, e.GetPosition(listBox)).Index;
                var targetIndex = insertionIndex > sourceIndex ? insertionIndex - 1 : insertionIndex;
                targetIndex = Math.Clamp(targetIndex, 0, actions.Items.Count - 1);
                if (sourceIndex != targetIndex) actions.Items.Move(sourceIndex, targetIndex);

                listBox.SelectedItem = item;
                e.DragEffects = DragDropEffects.Move;
            }
            finally
            {
                Clear();
            }
        }

        public override void Cancel(object? sender, RoutedEventArgs e) => Clear();

        public void Clear()
        {
            _scrollTimer?.Stop();
            _scrollTimer = null;
            _scrollViewer = null;
            _listBox = null;
            _scrollDirection = 0;
            if (_highlightedItem is not null) _highlightedItem.DropPosition = 0;
            _highlightedItem = null;
        }

        private void Scroll()
        {
            if (_scrollViewer is not { } scrollViewer || _listBox is not { } listBox) return;

            var maxOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            var offset = Math.Clamp(scrollViewer.Offset.Y + _scrollDirection * ScrollStep, 0, maxOffset);
            if (offset == scrollViewer.Offset.Y)
            {
                _scrollTimer?.Stop();
                return;
            }

            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, offset);
            // Realize the newly exposed rows before locating the insertion marker at the stationary pointer.
            listBox.UpdateLayout();
            UpdateIndicator(listBox, _pointerPosition);
        }

        private void UpdateIndicator(ListBox listBox, Point position)
        {
            var insertion = FindInsertion(listBox, position);
            if (_highlightedItem is not null && !ReferenceEquals(_highlightedItem, insertion.Item))
            {
                _highlightedItem.DropPosition = 0;
            }
            _highlightedItem = insertion.Item;
            if (_highlightedItem is not null) _highlightedItem.DropPosition = insertion.IsAfter ? 1 : -1;
        }

        private static (int Index, Item? Item, bool IsAfter) FindInsertion(ListBox listBox, Point position)
        {
            var insertionIndex = 0;
            var lastItem = default(Item);
            foreach (var container in listBox.GetRealizedContainers().OrderBy(listBox.IndexFromContainer))
            {
                if (container.TranslatePoint(default, listBox) is not { } origin) continue;
                var index = listBox.IndexFromContainer(container);
                if (position.Y < origin.Y + container.Bounds.Height / 2)
                {
                    return (index, container.DataContext as Item, false);
                }

                insertionIndex = index + 1;
                lastItem = container.DataContext as Item;
            }

            return (insertionIndex, lastItem, true);
        }
    }
}

using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Everywhere.Views;

/// <summary>
/// A pixel-scrolling vertical virtualizing panel for items with unknown and changing heights.
/// </summary>
public class VariableHeightVirtualizingStackPanel : VirtualizingPanel
{
    public static readonly StyledProperty<double> EstimatedItemHeightProperty =
        AvaloniaProperty.Register<VariableHeightVirtualizingStackPanel, double>(
            nameof(EstimatedItemHeight),
            96,
            validate: value => value > 0 && !double.IsNaN(value) && !double.IsInfinity(value));

    public static readonly StyledProperty<double> CacheLengthProperty =
        AvaloniaProperty.Register<VariableHeightVirtualizingStackPanel, double>(
            nameof(CacheLength),
            1.0,
            validate: value => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value));

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<VariableHeightVirtualizingStackPanel, double>(
            nameof(Spacing),
            validate: value => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value));

    public static readonly StyledProperty<double> HeightShrinkGuardThresholdProperty =
        AvaloniaProperty.Register<VariableHeightVirtualizingStackPanel, double>(
            nameof(HeightShrinkGuardThreshold),
            64,
            validate: value => value >= 0 && !double.IsNaN(value) && !double.IsInfinity(value));

    public double EstimatedItemHeight
    {
        get => GetValue(EstimatedItemHeightProperty);
        set => SetValue(EstimatedItemHeightProperty, value);
    }

    public double CacheLength
    {
        get => GetValue(CacheLengthProperty);
        set => SetValue(CacheLengthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public double HeightShrinkGuardThreshold
    {
        get => GetValue(HeightShrinkGuardThresholdProperty);
        set => SetValue(HeightShrinkGuardThresholdProperty, value);
    }

    private static readonly AttachedProperty<object?> RecycleKeyProperty =
        AvaloniaProperty.RegisterAttached<VariableHeightVirtualizingStackPanel, Control, object?>("RecycleKey");

    private static readonly object ItemIsItsOwnContainer = new();

    private readonly List<Slot> _slots = [];
    private readonly Dictionary<Control, int> _containerToIndex = [];
    private readonly Dictionary<object, Stack<Control>> _recyclePool = [];
    private readonly List<double> _prefixOffsets = [0];

    private Rect _viewport;
    private Rect _extendedViewport;
    private bool _hasViewport;
    private bool _isInLayout;
    private bool _isWaitingForViewportUpdate;
    private bool _isApplyingScrollOffsetCorrection;
    private double _runningEstimatedItemHeight;
    private double _pendingScrollOffsetCorrection;
    private double _pendingScrollOffsetCorrectionBaseY = double.NaN;
    private double _panelTopWithinScrollContent = double.NaN;
    private IDisposable? _scrollViewerOffsetSubscription;
    private ScrollViewer? _observedScrollViewer;
    private bool _isWaitingForShrinkConfirmation;
    private int _prefixDirtyIndex;
    private int _realizedStartIndex = -1;
    private int _realizedEndIndex = -1;
    private int _scrollToIndex = -1;
    private Control? _scrollToElement;

    static VariableHeightVirtualizingStackPanel()
    {
        EstimatedItemHeightProperty.Changed.AddClassHandler<VariableHeightVirtualizingStackPanel>((panel, args) =>
        {
            panel._runningEstimatedItemHeight = args.GetNewValue<double>();
            panel.MarkPrefixDirty(0);
            panel.InvalidateMeasure();
        });

        CacheLengthProperty.Changed.AddClassHandler<VariableHeightVirtualizingStackPanel>((panel, _) =>
        {
            panel.RecalculateExtendedViewport();
            panel.InvalidateMeasure();
        });

        SpacingProperty.Changed.AddClassHandler<VariableHeightVirtualizingStackPanel>((panel, _) =>
        {
            panel.MarkPrefixDirty(0);
            panel.InvalidateMeasure();
        });
    }

    public VariableHeightVirtualizingStackPanel()
    {
        _runningEstimatedItemHeight = EstimatedItemHeight;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        UpdateScrollViewerSubscription();

        var items = Items;
        var itemCount = items.Count;
        EnsureSlots(itemCount);

        if (itemCount == 0)
        {
            RecycleAll();
            return default;
        }

        _isInLayout = true;
        try
        {
            var viewport = GetMeasureViewport(availableSize);
            var anchor = CaptureAnchor(viewport.Top, itemCount);
            var (startIndex, endIndex) = GetRealizationRange(_extendedViewport, itemCount, anchor.Index);

            RealizeRange(items, startIndex, endIndex, availableSize);
            QueueAnchorCorrection(anchor, viewport.Top);

            var desired = new Size(GetDesiredWidth(availableSize), GetTotalHeight(itemCount));
            return desired;
        }
        finally
        {
            _isInLayout = false;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _scrollViewerOffsetSubscription?.Dispose();
        _scrollViewerOffsetSubscription = null;
        _observedScrollViewer = null;
        _panelTopWithinScrollContent = double.NaN;
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _isInLayout = true;
        try
        {
            if (HasRealizedRange)
            {
                var endIndex = Math.Min(_realizedEndIndex, _slots.Count - 1);
                for (var index = Math.Max(0, _realizedStartIndex); index <= endIndex; index++)
                {
                    var element = _slots[index].Container;
                    element?.Arrange(new Rect(0, GetOffsetForIndex(index), finalSize.Width, GetItemHeight(index)));
                }
            }

            ApplyPendingScrollOffsetCorrection();
            return finalSize;
        }
        finally
        {
            _isInLayout = false;
        }
    }

    protected override void OnItemsChanged(IReadOnlyList<object?> items, NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(items, e);

        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add:
                InsertSlots(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Remove:
                RecycleSlotRange(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                RemoveSlots(e.OldStartingIndex, e.OldItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Replace:
                ResetSlotRange(e.NewStartingIndex, e.NewItems?.Count ?? 0);
                break;
            default:
                RecycleAll();
                _slots.Clear();
                _prefixOffsets.Clear();
                _prefixOffsets.Add(0);
                _prefixDirtyIndex = 0;
                EnsureSlots(items.Count);
                break;
        }

        RebuildContainerIndex();
        InvalidateMeasure();
    }

    protected override IInputElement? GetControl(NavigationDirection direction, IInputElement? from, bool wrap)
    {
        var count = Items.Count;
        if (count == 0)
            return null;

        var index = from is Control control ? IndexFromContainer(control) : -1;
        var target = direction switch
        {
            NavigationDirection.First => 0,
            NavigationDirection.Last => count - 1,
            NavigationDirection.Next or NavigationDirection.Down => index + 1,
            NavigationDirection.Previous or NavigationDirection.Up => index - 1,
            _ => index
        };

        if (wrap)
        {
            if (target < 0) target = count - 1;
            if (target >= count) target = 0;
        }

        return target >= 0 && target < count ? ScrollIntoView(target) : from;
    }

    protected override IEnumerable<Control> GetRealizedContainers()
    {
        foreach (var index in GetRealizedIndexes())
        {
            if (_slots[index].Container is { } container)
                yield return container;
        }
    }

    private IEnumerable<int> GetRealizedIndexes()
    {
        if (!HasRealizedRange)
            yield break;

        var endIndex = Math.Min(_realizedEndIndex, _slots.Count - 1);
        for (var index = Math.Max(0, _realizedStartIndex); index <= endIndex; index++)
        {
            if (_slots[index].Container is not null)
                yield return index;
        }
    }

    protected override Control? ContainerFromIndex(int index)
    {
        if (index < 0 || index >= _slots.Count)
            return null;

        if (_scrollToIndex == index)
            return _scrollToElement;

        return _slots[index].Container;
    }

    protected override int IndexFromContainer(Control container)
    {
        return container == _scrollToElement ? _scrollToIndex : _containerToIndex.GetValueOrDefault(container, -1);
    }

    protected override Control? ScrollIntoView(int index)
    {
        var items = Items;
        if (_isInLayout || index < 0 || index >= items.Count || !IsEffectivelyVisible)
        {
            return null;
        }

        if (ContainerFromIndex(index) is { } realized)
        {
            realized.BringIntoView();
            return realized;
        }

        if (TopLevel.GetTopLevel(this) is not { } root)
        {
            return null;
        }

        var element = GetOrCreateElement(items, index);
        MeasureElement(element, index, new Size(Bounds.Width, double.PositiveInfinity));
        element.Arrange(new Rect(0, GetOffsetForIndex(index), element.DesiredSize.Width, GetItemHeight(index)));

        _scrollToIndex = index;
        _scrollToElement = element;

        if (!_viewport.Contains(new Rect(0, GetOffsetForIndex(index), element.DesiredSize.Width, GetItemHeight(index))))
        {
            _isWaitingForViewportUpdate = true;
            root.UpdateLayout();
            _isWaitingForViewportUpdate = false;
        }

        element.BringIntoView();
        root.UpdateLayout();
        element.BringIntoView();

        _scrollToIndex = -1;
        _scrollToElement = null;
        return element;
    }

    private Rect GetMeasureViewport(Size availableSize)
    {
        if (!_isWaitingForViewportUpdate && TryGetScrollViewerViewport(out var scrollViewport))
        {
            _viewport = scrollViewport;
            _hasViewport = true;
            RecalculateExtendedViewport();
            return _viewport;
        }

        if (_isWaitingForViewportUpdate && _hasViewport)
            return _viewport;

        if (_hasViewport && _viewport.Height > 0)
            return _viewport;

        var fallbackHeight = double.IsFinite(availableSize.Height) && availableSize.Height > 0 ? availableSize.Height : 800;
        var fallbackWidth = double.IsFinite(availableSize.Width) && availableSize.Width > 0 ? availableSize.Width : Bounds.Width;
        _viewport = new Rect(0, 0, fallbackWidth, fallbackHeight);
        _hasViewport = true;
        RecalculateExtendedViewport();
        return _viewport;
    }

    private bool TryGetScrollViewerViewport(out Rect viewport)
    {
        viewport = default;
        if (this.FindAncestorOfType<ScrollViewer>() is not { } scrollViewer ||
            scrollViewer.Viewport.Height <= 0 ||
            !double.IsFinite(scrollViewer.Viewport.Height))
        {
            return false;
        }

        var panelTop = GetPanelTopWithinScrollContent(scrollViewer);
        var top = Math.Max(0, scrollViewer.Offset.Y - panelTop);
        viewport = new Rect(0, top, Math.Max(Bounds.Width, scrollViewer.Viewport.Width), scrollViewer.Viewport.Height);
        return true;
    }

    private double GetPanelTopWithinScrollContent(ScrollViewer scrollViewer)
    {
        if (double.IsFinite(_panelTopWithinScrollContent) && scrollViewer.Offset.Y > 0.5)
            return _panelTopWithinScrollContent;

        var panelTop = double.PositiveInfinity;

        if (this.TranslatePoint(default, scrollViewer) is { } scrollViewerPosition)
        {
            panelTop = Math.Min(panelTop, Math.Max(0, scrollViewerPosition.Y + scrollViewer.Offset.Y));
        }

        if (scrollViewer.Content is Visual content &&
            this.TranslatePoint(default, content) is { } contentPosition)
        {
            panelTop = Math.Min(panelTop, Math.Max(0, contentPosition.Y));
        }

        if (!double.IsFinite(panelTop))
            panelTop = 0;

        _panelTopWithinScrollContent = panelTop;
        return _panelTopWithinScrollContent;
    }

    private Anchor CaptureAnchor(double viewportTop, int itemCount)
    {
        var (index, offset) = FindIndexAtOffset(viewportTop, itemCount);
        var anchor = new Anchor(index, Math.Max(0, viewportTop - offset));
        return anchor;
    }

    private void QueueAnchorCorrection(Anchor anchor, double oldViewportTop)
    {
        if (_isApplyingScrollOffsetCorrection || anchor.Index < 0 || anchor.Index >= _slots.Count)
        {
            return;
        }

        var desiredViewportTop = GetOffsetForIndex(anchor.Index) + anchor.Delta;
        var correction = desiredViewportTop - oldViewportTop;
        if (!(Math.Abs(correction) > double.Epsilon))
        {
            _pendingScrollOffsetCorrection += correction;
            if (double.IsNaN(_pendingScrollOffsetCorrectionBaseY) &&
                this.FindAncestorOfType<ScrollViewer>() is { } scrollViewer)
            {
                _pendingScrollOffsetCorrectionBaseY = scrollViewer.Offset.Y;
            }
        }
    }

    private (int StartIndex, int EndIndex) GetRealizationRange(Rect viewport, int itemCount, int anchorIndex)
    {
        var start = Math.Max(0, viewport.Top);
        var end = Math.Max(start, viewport.Bottom);
        var (startIndex, y) = FindIndexAtOffset(start, itemCount);
        var index = startIndex;

        do
        {
            y += GetSlotHeight(index);
            index++;
        }
        while (index < itemCount && y < end);

        var endIndex = Math.Min(itemCount - 1, Math.Max(startIndex, index - 1));
        if (anchorIndex >= 0)
        {
            startIndex = Math.Min(startIndex, anchorIndex);
            endIndex = Math.Max(endIndex, anchorIndex);
        }

        return (startIndex, endIndex);
    }

    private void RealizeRange(IReadOnlyList<object?> items, int startIndex, int endIndex, Size availableSize)
    {
        if (startIndex > endIndex)
        {
            RecycleRealizedRange();
            return;
        }

        if (HasRealizedRange)
        {
            var oldStart = _realizedStartIndex;
            var oldEnd = _realizedEndIndex;

            if (oldStart < startIndex)
            {
                var recycleEnd = Math.Min(oldEnd, startIndex - 1);
                RecycleSlotRange(oldStart, recycleEnd - oldStart + 1);
            }

            if (oldEnd > endIndex)
            {
                var recycleStart = Math.Max(oldStart, endIndex + 1);
                RecycleSlotRange(recycleStart, oldEnd - recycleStart + 1);
            }
        }

        var measureSize = new Size(availableSize.Width, double.PositiveInfinity);
        for (var index = startIndex; index <= endIndex; index++)
        {
            var element = _slots[index].Container ?? GetOrCreateElement(items, index);
            MeasureElement(element, index, measureSize);
        }

        _realizedStartIndex = startIndex;
        _realizedEndIndex = endIndex;
    }

    private Control GetOrCreateElement(IReadOnlyList<object?> items, int index)
    {
        var slot = _slots[index];
        if (slot.Container is not null)
        {
            return slot.Container;
        }

        var item = items[index];
        var generator = ItemContainerGenerator!;
        Control element;

        if (generator.NeedsContainer(item, index, out var recycleKey))
        {
            element = GetRecycledElement(item, index, recycleKey) ?? CreateElement(item, index, recycleKey);
        }
        else
        {
            element = GetItemAsOwnContainer((Control)item!, item, index);
            recycleKey = ItemIsItsOwnContainer;
        }

        slot.Container = element;
        slot.RecycleKey = recycleKey;
        _containerToIndex[element] = index;
        return element;
    }

    private Control? GetRecycledElement(object? item, int index, object? recycleKey)
    {
        if (recycleKey is null ||
            !_recyclePool.TryGetValue(recycleKey, out var pool) ||
            pool.Count == 0)
        {
            return null;
        }

        var recycled = pool.Pop();
        ItemContainerGenerator!.PrepareItemContainer(recycled, item, index);
        AddInternalChild(recycled);
        ItemContainerGenerator.ItemContainerPrepared(recycled, item, index);
        return recycled;
    }

    private Control CreateElement(object? item, int index, object? recycleKey)
    {
        var generator = ItemContainerGenerator!;
        var container = generator.CreateContainer(item, index, recycleKey);
        container.SetValue(RecycleKeyProperty, recycleKey);
        generator.PrepareItemContainer(container, item, index);
        AddInternalChild(container);
        generator.ItemContainerPrepared(container, item, index);
        return container;
    }

    private Control GetItemAsOwnContainer(Control control, object? item, int index)
    {
        ItemContainerGenerator!.PrepareItemContainer(control, control, index);
        if (!Equals(control.GetValue(RecycleKeyProperty), ItemIsItsOwnContainer))
            control.SetValue(RecycleKeyProperty, ItemIsItsOwnContainer);

        AddInternalChild(control);
        ItemContainerGenerator.ItemContainerPrepared(control, item, index);
        return control;
    }

    private void MeasureElement(Control element, int index, Size availableSize)
    {
        element.Measure(availableSize);
        var after = Math.Max(1, element.DesiredSize.Height);
        SetHeight(index, after);
    }

    private void RecycleSlot(int index)
    {
        if (index < 0 || index >= _slots.Count)
            return;

        var slot = _slots[index];
        var element = slot.Container;
        if (element is null)
            return;

        _containerToIndex.Remove(element);
        slot.Container = null;

        var recycleKey = slot.RecycleKey ?? element.GetValue(RecycleKeyProperty);
        slot.RecycleKey = null;

        if (Equals(recycleKey, ItemIsItsOwnContainer))
        {
            RemoveInternalChild(element);
            return;
        }

        if (ItemsControl is not null && Equals(KeyboardNavigation.GetTabOnceActiveElement(ItemsControl), element))
        {
            slot.Container = element;
            slot.RecycleKey = recycleKey;
            _containerToIndex[element] = index;
            return;
        }

        ItemContainerGenerator!.ClearItemContainer(element);
        RemoveInternalChild(element);

        if (recycleKey is null)
            return;

        if (!_recyclePool.TryGetValue(recycleKey, out var pool))
        {
            pool = new Stack<Control>();
            _recyclePool.Add(recycleKey, pool);
        }

        pool.Push(element);
    }

    private void RecycleSlotRange(int startIndex, int count)
    {
        if (count <= 0)
            return;

        for (var index = startIndex; index < startIndex + count && index < _slots.Count; index++)
        {
            RecycleSlot(index);
        }
    }

    private void RecycleRealizedRange()
    {
        if (!HasRealizedRange)
            return;

        RecycleSlotRange(_realizedStartIndex, _realizedEndIndex - _realizedStartIndex + 1);
        ResetRealizedRange();
    }

    private void RecycleAll()
    {
        for (var index = 0; index < _slots.Count; index++)
        {
            RecycleSlot(index);
        }

        _containerToIndex.Clear();
        ResetRealizedRange();
    }

    private void EnsureSlots(int count)
    {
        while (_slots.Count < count)
        {
            _slots.Add(new Slot());
            MarkPrefixDirty(_slots.Count - 1);
        }

        if (_slots.Count > count)
        {
            RecycleSlotRange(count, _slots.Count - count);
            _slots.RemoveRange(count, _slots.Count - count);
            MarkPrefixDirty(count);
            ClampRealizedRange();
        }
    }

    private void InsertSlots(int index, int count)
    {
        if (count <= 0)
            return;

        while (_slots.Count < index)
        {
            _slots.Add(new Slot());
        }

        _slots.InsertRange(Math.Min(index, _slots.Count), Enumerable.Range(0, count).Select(_ => new Slot()));
        RefreshRealizedRange();
        MarkPrefixDirty(index);
    }

    private void RemoveSlots(int index, int count)
    {
        if (count <= 0 || index < 0 || index >= _slots.Count)
            return;

        var removeCount = Math.Min(count, _slots.Count - index);
        _slots.RemoveRange(index, removeCount);
        RefreshRealizedRange();
        MarkPrefixDirty(index);
    }

    private void ResetSlotRange(int index, int count)
    {
        if (count <= 0)
            return;

        EnsureSlotCountAtLeast(index + count);
        for (var i = index; i < index + count && i < _slots.Count; i++)
        {
            RecycleSlot(i);
            _slots[i].MeasuredHeight = double.NaN;
            _slots[i].PendingShrinkHeight = double.NaN;
        }

        RefreshRealizedRange();
        MarkPrefixDirty(index);
    }

    private void RebuildContainerIndex()
    {
        _containerToIndex.Clear();
        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index].Container is { } container)
                _containerToIndex[container] = index;
        }
    }

    private bool HasRealizedRange =>
        _realizedStartIndex >= 0 &&
        _realizedEndIndex >= _realizedStartIndex;

    private void ResetRealizedRange()
    {
        _realizedStartIndex = -1;
        _realizedEndIndex = -1;
    }

    private void ClampRealizedRange()
    {
        if (!HasRealizedRange)
            return;

        if (_realizedStartIndex >= _slots.Count)
        {
            ResetRealizedRange();
            return;
        }

        _realizedEndIndex = Math.Min(_realizedEndIndex, _slots.Count - 1);
        if (_realizedEndIndex < _realizedStartIndex)
            ResetRealizedRange();
    }

    private void RefreshRealizedRange()
    {
        var start = -1;
        var end = -1;
        for (var index = 0; index < _slots.Count; index++)
        {
            if (_slots[index].Container is null)
                continue;

            if (start < 0)
                start = index;

            end = index;
        }

        _realizedStartIndex = start;
        _realizedEndIndex = end;
    }

    private double GetItemHeight(int index)
    {
        if (index >= 0 && index < _slots.Count && _slots[index].HasMeasuredHeight)
            return _slots[index].MeasuredHeight;

        return _runningEstimatedItemHeight;
    }

    private double GetSlotHeight(int index)
    {
        var spacing = index < Items.Count - 1 ? Spacing : 0;
        return GetItemHeight(index) + spacing;
    }

    private void SetHeight(int index, double height)
    {
        EnsureSlotCountAtLeast(index + 1);
        var slot = _slots[index];
        if (slot.HasMeasuredHeight && Math.Abs(height - slot.MeasuredHeight) <= double.Epsilon)
        {
            slot.PendingShrinkHeight = double.NaN;
            return;
        }

        if (slot.HasMeasuredHeight &&
            height + HeightShrinkGuardThreshold < slot.MeasuredHeight)
        {
            if (slot.HasPendingShrinkHeight &&
                Math.Abs(slot.PendingShrinkHeight - height) <= double.Epsilon)
            {
                slot.PendingShrinkHeight = double.NaN;
                slot.MeasuredHeight = height;
                MarkPrefixDirty(index);
                return;
            }

            slot.PendingShrinkHeight = height;
            RequestShrinkConfirmation();
            return;
        }

        slot.PendingShrinkHeight = double.NaN;
        slot.MeasuredHeight = height;
        MarkPrefixDirty(index);
    }

    private void RequestShrinkConfirmation()
    {
        if (_isWaitingForShrinkConfirmation)
            return;

        _isWaitingForShrinkConfirmation = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _isWaitingForShrinkConfirmation = false;
                if (VisualRoot is not null)
                {
                    InvalidateMeasure();
                }
            },
            DispatcherPriority.Background);
    }

    private void EnsureSlotCountAtLeast(int count)
    {
        while (_slots.Count < count)
        {
            _slots.Add(new Slot());
            MarkPrefixDirty(_slots.Count - 1);
        }
    }

    private double GetOffsetForIndex(int index)
    {
        var count = Items.Count;
        EnsurePrefixOffsets(count);
        return _prefixOffsets[Math.Clamp(index, 0, count)];
    }

    private double GetTotalHeight(int itemCount)
    {
        EnsurePrefixOffsets(itemCount);
        return _prefixOffsets[itemCount];
    }

    private (int Index, double Offset) FindIndexAtOffset(double offset, int itemCount)
    {
        EnsurePrefixOffsets(itemCount);

        if (itemCount == 0 || offset <= 0)
            return (0, 0);

        if (offset >= _prefixOffsets[itemCount])
        {
            var lastIndex = Math.Max(0, itemCount - 1);
            return (lastIndex, _prefixOffsets[lastIndex]);
        }

        var low = 0;
        var high = itemCount - 1;
        while (low < high)
        {
            var mid = low + (high - low) / 2;
            if (_prefixOffsets[mid + 1] <= offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return (low, _prefixOffsets[low]);
    }

    private void EnsurePrefixOffsets(int itemCount)
    {
        while (_prefixOffsets.Count <= itemCount)
        {
            _prefixOffsets.Add(0);
        }

        if (_prefixOffsets.Count > itemCount + 1)
        {
            _prefixOffsets.RemoveRange(itemCount + 1, _prefixOffsets.Count - itemCount - 1);
        }

        var start = Math.Clamp(_prefixDirtyIndex, 0, itemCount);
        if (start == 0)
            _prefixOffsets[0] = 0;

        for (var i = start; i < itemCount; i++)
        {
            _prefixOffsets[i + 1] = _prefixOffsets[i] + GetSlotHeight(i);
        }

        _prefixDirtyIndex = itemCount;
    }

    private void MarkPrefixDirty(int index)
    {
        _prefixDirtyIndex = Math.Min(_prefixDirtyIndex, Math.Max(0, index));
    }

    private double GetDesiredWidth(Size availableSize)
    {
        if (double.IsFinite(availableSize.Width))
            return availableSize.Width;

        return GetRealizedIndexes()
            .Select(index => _slots[index].Container?.DesiredSize.Width ?? 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        if (TryGetScrollViewerViewport(out _))
            return;

        _viewport = e.EffectiveViewport.Intersect(new Rect(Bounds.Size));
        _hasViewport = true;
        _isWaitingForViewportUpdate = false;
        RecalculateExtendedViewport();
        InvalidateMeasure();
    }

    private void UpdateScrollViewerSubscription()
    {
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (ReferenceEquals(scrollViewer, _observedScrollViewer))
            return;

        _scrollViewerOffsetSubscription?.Dispose();
        _scrollViewerOffsetSubscription = null;
        _observedScrollViewer = scrollViewer;

        if (scrollViewer is null)
        {
            return;
        }

        _scrollViewerOffsetSubscription = scrollViewer
            .GetObservable(ScrollViewer.OffsetProperty)
            .Subscribe(_ =>
            {
                if (!_isApplyingScrollOffsetCorrection)
                    InvalidateMeasure();
            });
    }

    private void RecalculateExtendedViewport()
    {
        if (!_hasViewport)
            return;

        var totalHeight = Math.Max(GetTotalHeight(Items.Count), _viewport.Bottom);
        var buffer = _viewport.Height * CacheLength;
        var top = Math.Max(0, _viewport.Top - buffer);
        var bottom = Math.Min(totalHeight, _viewport.Bottom + buffer);

        _extendedViewport = new Rect(_viewport.X, top, _viewport.Width, Math.Max(0, bottom - top));
    }

    private void ApplyPendingScrollOffsetCorrection()
    {
        if (Math.Abs(_pendingScrollOffsetCorrection) <= double.Epsilon)
        {
            return;
        }

        var correction = _pendingScrollOffsetCorrection;
        _pendingScrollOffsetCorrection = 0;

        if (this.FindAncestorOfType<ScrollViewer>() is not { } scrollViewer)
        {
            _pendingScrollOffsetCorrectionBaseY = double.NaN;
            return;
        }

        var baseY = _pendingScrollOffsetCorrectionBaseY;
        Dispatcher.UIThread.Post(
            () =>
            {
                if (!scrollViewer.IsAttachedToVisualTree())
                {
                    return;
                }

                if (double.IsFinite(baseY) &&
                    !(Math.Abs(scrollViewer.Offset.Y - baseY) <= double.Epsilon))
                {
                    _pendingScrollOffsetCorrectionBaseY = double.NaN;
                    return;
                }

                _pendingScrollOffsetCorrectionBaseY = double.NaN;
                _isApplyingScrollOffsetCorrection = true;
                try
                {
                    var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                    var y = Math.Clamp(scrollViewer.Offset.Y + correction, 0, maximum);
                    scrollViewer.Offset = new Vector(scrollViewer.Offset.X, y);
                }
                finally
                {
                    _isApplyingScrollOffsetCorrection = false;
                }
            },
            DispatcherPriority.Background);
    }

    private sealed class Slot
    {
        public double MeasuredHeight { get; set; } = double.NaN;
        public double PendingShrinkHeight { get; set; } = double.NaN;
        public Control? Container { get; set; }
        public object? RecycleKey { get; set; }
        public bool HasMeasuredHeight => !double.IsNaN(MeasuredHeight);
        public bool HasPendingShrinkHeight => !double.IsNaN(PendingShrinkHeight);
    }

    private readonly record struct Anchor(int Index, double Delta);
}
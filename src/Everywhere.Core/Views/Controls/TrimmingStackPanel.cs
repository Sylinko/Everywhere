using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Everywhere.Views;

/// <summary>
/// Arranges the largest leading sequence of optional children that fits between required leading
/// and trailing children, and gives compressed children a finite measure constraint.
/// </summary>
/// <remarks>
/// <para>
/// A normal <see cref="StackPanel"/> measures children with infinite space along its orientation.
/// That is useful for scrolling surfaces, but it prevents a nested <see cref="TextBlock"/> from
/// applying text trimming. This variant preserves the StackPanel child order, orientation, spacing,
/// cross-axis sizing, and navigation behavior while keeping its stacking-axis size finite.
/// </para>
/// <para>
/// Overflow children are arranged into an empty slot instead of having <see cref="Visual.IsVisible"/>
/// changed. Their bindings remain untouched and they return automatically when more space becomes
/// available. <see cref="MinimumLeadingVisibleItems"/> and
/// <see cref="MinimumTrailingVisibleItems"/> define the two retained edge regions. Optional middle
/// children are admitted from the leading side in source order only while the entire retained set
/// fits.
/// </para>
/// <para>
/// When the required edge regions do not fit naturally, trailing children keep their natural size
/// whenever possible because they commonly contain status icons or actions. Leading children share
/// the remaining finite space and can apply text trimming. If the trailing region alone cannot fit,
/// all retained children share the available content space. An impossibly small slot can therefore
/// reduce children to zero length, but retained controls never overlap one another.
/// </para>
/// </remarks>
public sealed class TrimmingStackPanel : StackPanel
{
    /// <summary>
    /// Defines the <see cref="MinimumLeadingVisibleItems"/> property.
    /// </summary>
    public static readonly StyledProperty<int> MinimumLeadingVisibleItemsProperty =
        AvaloniaProperty.Register<TrimmingStackPanel, int>(
            nameof(MinimumLeadingVisibleItems),
            1,
            validate: value => value >= 0);

    /// <summary>
    /// Defines the <see cref="MinimumTrailingVisibleItems"/> property.
    /// </summary>
    public static readonly StyledProperty<int> MinimumTrailingVisibleItemsProperty =
        AvaloniaProperty.Register<TrimmingStackPanel, int>(
            nameof(MinimumTrailingVisibleItems),
            0,
            validate: value => value >= 0);

    private readonly List<Control> _visibleChildren = [];
    private readonly List<double> _naturalLengths = [];
    private readonly List<Control> _retainedChildren = [];
    private readonly List<double> _retainedLengths = [];
    private int _retainedTrailingCount;
    private bool _usedFiniteMeasure;

    static TrimmingStackPanel()
    {
        AffectsMeasure<TrimmingStackPanel>(
            MinimumLeadingVisibleItemsProperty,
            MinimumTrailingVisibleItemsProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TrimmingStackPanel>(true);
    }

    /// <summary>
    /// Gets or sets the minimum number of visible children retained from the leading edge when the
    /// natural content overflows. The default is one.
    /// </summary>
    public int MinimumLeadingVisibleItems
    {
        get => GetValue(MinimumLeadingVisibleItemsProperty);
        set => SetValue(MinimumLeadingVisibleItemsProperty, value);
    }

    /// <summary>
    /// Gets or sets the minimum number of visible children retained from the trailing edge when the
    /// natural content overflows. The default is zero.
    /// </summary>
    public int MinimumTrailingVisibleItems
    {
        get => GetValue(MinimumTrailingVisibleItemsProperty);
        set => SetValue(MinimumTrailingVisibleItemsProperty, value);
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var horizontal = Orientation == Orientation.Horizontal;
        var availableLength = GetLength(availableSize, horizontal);

        // There is no meaningful overflow decision when the parent itself offers infinite space.
        // Falling back to StackPanel also preserves its exact size-to-content behavior in scrollers.
        if (!double.IsFinite(availableLength))
        {
            _usedFiniteMeasure = false;
            ClearMeasureState();
            return base.MeasureOverride(availableSize);
        }

        _usedFiniteMeasure = true;
        ClearMeasureState();

        var naturalConstraint = WithLength(availableSize, horizontal, double.PositiveInfinity);

        // Natural sizes decide which complete children fit. Measuring every child also follows the
        // normal Panel contract and ensures a previously omitted child is ready to return after a
        // width change.
        foreach (var child in Children)
        {
            child.Measure(naturalConstraint);
            if (!child.IsVisible) continue;

            _visibleChildren.Add(child);
            _naturalLengths.Add(GetLength(child.DesiredSize, horizontal));
        }

        var visibleCount = _visibleChildren.Count;
        var leadingCount = Math.Min(MinimumLeadingVisibleItems, visibleCount);
        var trailingStart = Math.Max(0, visibleCount - MinimumTrailingVisibleItems);
        var mandatoryCount = leadingCount + Math.Max(0, visibleCount - Math.Max(leadingCount, trailingStart));
        var mandatoryChildrenLength = 0d;

        for (var i = 0; i < visibleCount; i++)
        {
            if (i < leadingCount || i >= trailingStart)
                mandatoryChildrenLength += _naturalLengths[i];
        }

        var retainedCount = mandatoryCount;
        var retainedChildrenLength = mandatoryChildrenLength;
        var retainedPrefixCount = leadingCount;

        // Preserve ordinary StackPanel order for optional content: once one middle child cannot fit,
        // later middle children are not considered even if individually smaller. The reserved tail
        // is included in every candidate so admitting a middle child can never evict it.
        for (var i = leadingCount; i < trailingStart; i++)
        {
            var candidateCount = retainedCount + 1;
            var candidateLength = retainedChildrenLength + _naturalLengths[i] +
                                  Spacing * Math.Max(0, candidateCount - 1);
            if (candidateLength > availableLength) break;

            retainedCount = candidateCount;
            retainedChildrenLength += _naturalLengths[i];
            retainedPrefixCount++;
        }

        for (var i = 0; i < retainedPrefixCount; i++)
            Retain(i);

        var retainedTailStart = Math.Max(retainedPrefixCount, trailingStart);
        for (var i = retainedTailStart; i < visibleCount; i++)
            Retain(i);

        // An item can satisfy both edge minima when the requested regions overlap. Classify that
        // overlap as trailing as well so the caller's tail-retention intent still wins compression
        // priority instead of being lost merely because the child was inserted with the prefix.
        _retainedTrailingCount = Math.Min(_retainedChildren.Count, visibleCount - trailingStart);

        var naturalLength = CalculateRetainedLength();
        if (naturalLength > availableLength)
            ConstrainRequiredItems(availableSize, horizontal, availableLength);

        return CalculateDesiredSize(horizontal);

        void Retain(int index)
        {
            _retainedChildren.Add(_visibleChildren[index]);
            _retainedLengths.Add(_naturalLengths[index]);
        }
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!_usedFiniteMeasure)
            return base.ArrangeOverride(finalSize);

        var horizontal = Orientation == Orientation.Horizontal;
        var offset = 0d;
        var retainedIndex = 0;

        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;

            if (retainedIndex >= _retainedChildren.Count ||
                !ReferenceEquals(child, _retainedChildren[retainedIndex]))
            {
                // Empty bounds remove the complete overflow control from rendering and hit testing
                // without changing its public visibility value or disturbing an existing binding.
                child.Arrange(default);
                continue;
            }

            var length = _retainedLengths[retainedIndex++];
            var rect = horizontal ?
                new Rect(offset, 0, length, Math.Max(finalSize.Height, child.DesiredSize.Height)) :
                new Rect(0, offset, Math.Max(finalSize.Width, child.DesiredSize.Width), length);
            child.Arrange(rect);
            offset += length + Spacing;
        }

        RaiseEvent(new RoutedEventArgs(horizontal ? HorizontalSnapPointsChangedEvent : VerticalSnapPointsChangedEvent));

        return finalSize;
    }

    private void ConstrainRequiredItems(Size availableSize, bool horizontal, double availableLength)
    {
        // Spacing retains ordinary StackPanel semantics. If it alone consumes the available length,
        // children receive zero-sized slots; there is no honest way to display positive content in
        // that geometry without overlapping controls.
        var contentLength = Math.Max(0, availableLength - Spacing * Math.Max(0, _retainedLengths.Count - 1));
        var trailingStart = _retainedLengths.Count - _retainedTrailingCount;
        var trailingLength = 0d;
        for (var i = trailingStart; i < _retainedLengths.Count; i++)
            trailingLength += _retainedLengths[i];

        if (trailingStart > 0 && trailingLength <= contentLength)
        {
            // A reserved tail commonly contains a chevron, state glyph, or compact action. Keep it
            // crisp at its natural size and let leading labels consume the finite remainder first.
            ConstrainRange(availableSize, horizontal, 0, trailingStart, contentLength - trailingLength);
            return;
        }

        // The tail alone cannot fit (or every retained child belongs to it). At that point no child
        // can honestly be guaranteed its natural size, so sharing the slot is the only deterministic
        // policy that preserves order and avoids overlap.
        ConstrainRange(availableSize, horizontal, 0, _retainedLengths.Count, contentLength);
    }

    private void ConstrainRange(
        Size availableSize,
        bool horizontal,
        int start,
        int end,
        double availableLength)
    {
        var remainingLength = availableLength;
        var remainingItems = end - start;
        for (var i = start; i < end; i++)
        {
            // Cap each item at an equal share of the remaining space. Naturally short children keep
            // their desired length, leaving the unused portion for later items; long children are
            // remeasured and can propagate the finite constraint to TextBlock.TextTrimming.
            var fairShare = remainingItems == 0 ? 0 : remainingLength / remainingItems;
            var constrainedLength = Math.Min(_retainedLengths[i], Math.Max(0, fairShare));
            _retainedChildren[i].Measure(WithLength(availableSize, horizontal, constrainedLength));

            var measuredLength = Math.Min(GetLength(_retainedChildren[i].DesiredSize, horizontal), constrainedLength);
            _retainedLengths[i] = measuredLength;
            remainingLength = Math.Max(0, remainingLength - measuredLength);
            remainingItems--;
        }
    }

    private Size CalculateDesiredSize(bool horizontal)
    {
        var desiredLength = CalculateRetainedLength();
        var desiredCrossLength = 0d;
        foreach (var child in _retainedChildren)
            desiredCrossLength = Math.Max(desiredCrossLength, GetCrossLength(child.DesiredSize, horizontal));

        return horizontal ? new Size(desiredLength, desiredCrossLength) : new Size(desiredCrossLength, desiredLength);
    }

    private double CalculateRetainedLength()
    {
        var length = Spacing * Math.Max(0, _retainedLengths.Count - 1);
        foreach (var retainedLength in _retainedLengths) length += retainedLength;
        return length;
    }

    private void ClearMeasureState()
    {
        _visibleChildren.Clear();
        _naturalLengths.Clear();
        _retainedChildren.Clear();
        _retainedLengths.Clear();
        _retainedTrailingCount = 0;
    }

    private static double GetLength(Size size, bool horizontal) => horizontal ? size.Width : size.Height;

    private static double GetCrossLength(Size size, bool horizontal) => horizontal ? size.Height : size.Width;

    private static Size WithLength(Size size, bool horizontal, double length) => horizontal ? size.WithWidth(length) : size.WithHeight(length);
}

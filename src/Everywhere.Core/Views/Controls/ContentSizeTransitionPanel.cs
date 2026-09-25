using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace Everywhere.Views;

/// <summary>
/// Lays out the two presenters owned by TransitioningContentControl while optionally animating
/// their viewport size. Presenter visibility and content lifetime remain owned by the host.
/// </summary>
public sealed class ContentSizeTransitionPanel : Panel
{
    public static readonly StyledProperty<bool> AnimateContentSizeProperty =
        ConditionalContentControl.AnimateContentSizeProperty.AddOwner<ContentSizeTransitionPanel>();

    public bool AnimateContentSize
    {
        get => GetValue(AnimateContentSizeProperty);
        set => SetValue(AnimateContentSizeProperty, value);
    }

    public static readonly StyledProperty<TimeSpan> ContentSizeTransitionDurationProperty =
        ConditionalContentControl.ContentSizeTransitionDurationProperty.AddOwner<ContentSizeTransitionPanel>();

    public TimeSpan ContentSizeTransitionDuration
    {
        get => GetValue(ContentSizeTransitionDurationProperty);
        set => SetValue(ContentSizeTransitionDurationProperty, value);
    }

    public static readonly StyledProperty<Easing> ContentSizeTransitionEasingProperty =
        ConditionalContentControl.ContentSizeTransitionEasingProperty.AddOwner<ContentSizeTransitionPanel>();

    public Easing ContentSizeTransitionEasing
    {
        get => GetValue(ContentSizeTransitionEasingProperty);
        set => SetValue(ContentSizeTransitionEasingProperty, value);
    }

    public static readonly StyledProperty<object?> TargetContentProperty =
        AvaloniaProperty.Register<ContentSizeTransitionPanel, object?>(nameof(TargetContent));

    public object? TargetContent
    {
        get => GetValue(TargetContentProperty);
        set => SetValue(TargetContentProperty, value);
    }

    private readonly Dictionary<Control, Size> _pageSizes = new();
    private TopLevel? _topLevel;
    private Size _availableSize;
    private Size _currentSize;
    private Size _fromSize;
    private Size _targetSize;
    private TimeSpan? _startedAt;
    private bool _hasArranged;
    private bool _isAnimating;
    private bool _frameRequested;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _topLevel = TopLevel.GetTopLevel(this);
        InvalidateMeasure();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _topLevel = null;
        _hasArranged = false;
        _isAnimating = false;
        _startedAt = null;
        _pageSizes.Clear();
        // A queued frame cannot be cancelled. Keep its pending flag until that callback runs,
        // including when this panel is reattached before the callback arrives.
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetContentProperty ||
            change.Property == AnimateContentSizeProperty ||
            change.Property == ContentSizeTransitionDurationProperty ||
            change.Property == ContentSizeTransitionEasingProperty ||
            change.Property == IsEnabledProperty ||
            change.Property == IsVisibleProperty)
        {
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!AnimateContentSize)
        {
            _isAnimating = false;
            _startedAt = null;
            _pageSizes.Clear();
            return base.MeasureOverride(availableSize);
        }

        var constraintsChanged = _availableSize != availableSize;
        _availableSize = availableSize;
        var targetSize = default(Size);
        foreach (var child in Children)
        {
            if (!child.IsVisible)
                continue;

            if (child is ContentPresenter presenter && ReferenceEquals(presenter.Content, TargetContent))
            {
                // Discover the endpoint under real parent constraints, then measure and arrange
                // at that endpoint. Animation frames never feed the viewport size into the page.
                // Only new content, invalidated page layout or changed constraints require discovery.
                if (!_pageSizes.TryGetValue(child, out var pageSize) || constraintsChanged || !child.IsMeasureValid)
                {
                    child.Measure(availableSize);
                    pageSize = child.DesiredSize;
                }

                child.Measure(pageSize);
                targetSize = pageSize;
                _pageSizes[child] = targetSize;
            }
            else
            {
                // The outgoing page keeps its own endpoint layout even while the viewport shrinks.
                var pageSize = _pageSizes.TryGetValue(child, out var previousSize) ? previousSize : child.Bounds.Size;
                pageSize = Constrain(pageSize, availableSize);
                child.Measure(pageSize);
                _pageSizes[child] = pageSize;
            }
        }

        if (!_hasArranged || _topLevel is null || !IsEnabled || ContentSizeTransitionDuration <= TimeSpan.Zero)
        {
            _isAnimating = false;
            _startedAt = null;
            _currentSize = _targetSize = targetSize;
        }
        else if (_targetSize != targetSize)
        {
            // Retarget from the currently displayed viewport, including during an interrupted slide.
            _fromSize = Constrain(Bounds.Size, availableSize);
            _currentSize = _fromSize;
            _targetSize = targetSize;
            _startedAt = null;
            _isAnimating = _fromSize != _targetSize;
            RequestFrame();
        }

        _currentSize = Constrain(_currentSize, availableSize);
        return _currentSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (!AnimateContentSize)
        {
            _currentSize = _targetSize = finalSize;
            _hasArranged = true;
            return base.ArrangeOverride(finalSize);
        }

        foreach (var child in Children)
        {
            if (child.IsVisible && _pageSizes.TryGetValue(child, out var pageSize))
                child.Arrange(new Rect(pageSize));
        }

        _hasArranged = true;
        return finalSize;
    }

    private void RequestFrame()
    {
        if (!_isAnimating || _frameRequested || _topLevel is null)
            return;

        _frameRequested = true;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        _frameRequested = false;
        if (!_isAnimating || _topLevel is null)
            return;

        if (!AnimateContentSize || !IsEnabled || !IsEffectivelyVisible || ContentSizeTransitionDuration <= TimeSpan.Zero)
        {
            _currentSize = _targetSize;
            _isAnimating = false;
            _startedAt = null;
            InvalidateMeasure();
            return;
        }

        _startedAt ??= time;
        var progress = Math.Clamp((time - _startedAt.Value).TotalSeconds / ContentSizeTransitionDuration.TotalSeconds, 0d, 1d);
        var eased = Math.Clamp(ContentSizeTransitionEasing.Ease(progress), 0d, 1d);
        _currentSize = new Size(
            _fromSize.Width + (_targetSize.Width - _fromSize.Width) * eased,
            _fromSize.Height + (_targetSize.Height - _fromSize.Height) * eased);

        if (progress >= 1d)
        {
            _currentSize = _targetSize;
            _isAnimating = false;
            _startedAt = null;
        }

        InvalidateMeasure();
        RequestFrame();
    }

    private static Size Constrain(Size size, Size availableSize) =>
        new(Math.Min(size.Width, availableSize.Width), Math.Min(size.Height, availableSize.Height));
}
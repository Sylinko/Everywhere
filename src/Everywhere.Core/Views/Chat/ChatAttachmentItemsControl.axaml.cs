using System.Diagnostics.CodeAnalysis;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;

namespace Everywhere.Views;

[TemplatePart("PART_ItemsControl", typeof(ChatAttachmentItemsPresenter), IsRequired = true)]
public class ChatAttachmentItemsControl : TemplatedControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        ItemsControl.ItemsSourceProperty.AddOwner<ChatAttachmentItemsControl>();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly StyledProperty<Modalities> SupportedModalitiesProperty =
        AvaloniaProperty.Register<ChatAttachmentItemsControl, Modalities>(nameof(SupportedModalities));

    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RemoveCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatAttachment>?> RemoveCommandProperty =
        AvaloniaProperty.Register<ChatAttachmentItemsControl, IRelayCommand<ChatAttachment>?>(nameof(RemoveCommand));

    /// <summary>
    /// Gets or sets the command to remove an attachment.
    /// </summary>
    public IRelayCommand<ChatAttachment>? RemoveCommand
    {
        get => GetValue(RemoveCommandProperty);
        set => SetValue(RemoveCommandProperty, value);
    }

    private ChatAttachmentItemsPresenter? _itemsControl;
    private readonly Dictionary<VisualElementAttachment, VisualElementAttachmentPresentation> _presentations =
        new(ReferenceEqualityComparer.Instance);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        if (_itemsControl is not null) _itemsControl.Owner = null;
        _itemsControl = e.NameScope.Find<ChatAttachmentItemsPresenter>("PART_ItemsControl");
        _itemsControl?.Owner = this;
    }

    public void RegisterPendingAttachment(VisualElementAttachment attachment)
    {
        _presentations[attachment] = VisualElementAttachmentPresentation.Pending;
        ApplyPresentation(attachment);
    }

    public bool TryGetAttachmentBoundsOnScreen(VisualElementAttachment attachment, out PixelRect bounds)
    {
        bounds = default;

        if (_itemsControl?.TryGetAttachmentContainer(attachment, out var target) is not true ||
            target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
        {
            return false;
        }

        var topLeft = target.PointToScreen(default);
        var bottomRight = target.PointToScreen(new Point(target.Bounds.Width, target.Bounds.Height));
        if (bottomRight.X <= topLeft.X || bottomRight.Y <= topLeft.Y) return false;

        bounds = new PixelRect(topLeft, bottomRight);
        return true;
    }

    public bool BeginAttachmentAcceptance(VisualElementAttachment attachment)
    {
        if (!_presentations.ContainsKey(attachment) ||
            _itemsControl?.TryGetAttachmentContainer(attachment, out var target) is not true)
        {
            return false;
        }

        _presentations[attachment] = VisualElementAttachmentPresentation.Accepting;
        ChatAttachmentItemsPresenter.ApplyPresentation(target, VisualElementAttachmentPresentation.Accepting);
        _itemsControl.ScrollIntoView(attachment);
        target.BringIntoView();
        return true;
    }

    public void CompleteAttachmentAcceptance(VisualElementAttachment attachment) =>
        ReleaseAttachment(attachment);

    public void CancelAttachmentAcceptance(VisualElementAttachment attachment) =>
        ReleaseAttachment(attachment);

    public bool IsAttachmentReady(VisualElementAttachment attachment, out PixelRect bounds)
    {
        bounds = default;
        if (GetPresentation(attachment) != VisualElementAttachmentPresentation.Accepting ||
            _itemsControl is not { IsMeasureValid: true, IsArrangeValid: true } ||
            _itemsControl?.TryGetAttachmentContainer(attachment, out var target) is not true ||
            target is not { IsMeasureValid: true, IsArrangeValid: true } ||
            !TryGetAttachmentBoundsOnScreen(attachment, out bounds))
        {
            return false;
        }

        var ancestors = _itemsControl.GetVisualAncestors().ToArray();
        var scrollViewer = ancestors.OfType<ScrollViewer>().FirstOrDefault();
        var sizeTransition = ancestors.OfType<ChatAttachmentSizeTransitionContainer>().FirstOrDefault();
        return IsInsideViewport(scrollViewer, bounds) && IsInsideViewport(sizeTransition, bounds);
    }

    private static bool IsInsideViewport(Control? viewport, PixelRect bounds)
    {
        if (viewport is null) return true;

        var viewportTopLeft = viewport.PointToScreen(default);
        var viewportBottomRight = viewport.PointToScreen(new Point(viewport.Bounds.Width, viewport.Bounds.Height));
        const int tolerance = 1;
        return bounds.X >= viewportTopLeft.X - tolerance &&
            bounds.Y >= viewportTopLeft.Y - tolerance &&
            bounds.Right <= viewportBottomRight.X + tolerance &&
            bounds.Bottom <= viewportBottomRight.Y + tolerance;
    }

    private void ReleaseAttachment(VisualElementAttachment attachment)
    {
        _presentations.Remove(attachment);
        ApplyPresentation(attachment);
    }

    public VisualElementAttachmentPresentation GetPresentation(VisualElementAttachment attachment) =>
        _presentations.GetValueOrDefault(attachment);

    private void ApplyPresentation(VisualElementAttachment attachment)
    {
        if (_itemsControl?.TryGetAttachmentContainer(attachment, out var target) is true)
            ChatAttachmentItemsPresenter.ApplyPresentation(target, GetPresentation(attachment));
    }

}

public enum VisualElementAttachmentPresentation
{
    Normal,
    Pending,
    Accepting
}

/// <summary>
/// Applies the pick lifecycle to generated item containers while a standard panel owns layout.
/// </summary>
public sealed class ChatAttachmentItemsPresenter : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    public ChatAttachmentItemsControl? Owner { get; internal set; }

    private readonly Dictionary<VisualElementAttachment, Control> _containers = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Control, VisualElementAttachment> _attachments = new(ReferenceEqualityComparer.Instance);

    protected override void ContainerForItemPreparedOverride(Control container, object? item, int index)
    {
        base.ContainerForItemPreparedOverride(container, item, index);
        if (item is VisualElementAttachment attachment)
        {
            _containers[attachment] = container;
            _attachments[container] = attachment;
            ApplyPresentation(container, Owner?.GetPresentation(attachment) ?? VisualElementAttachmentPresentation.Normal);
        }
        else
        {
            ApplyPresentation(container, VisualElementAttachmentPresentation.Normal);
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        if (_attachments.Remove(container, out var attachment)) _containers.Remove(attachment);
        ApplyPresentation(container, VisualElementAttachmentPresentation.Normal);
        base.ClearContainerForItemOverride(container);
    }

    public bool TryGetAttachmentContainer(VisualElementAttachment attachment, [NotNullWhen(true)] out Control? container)
    {
        return _containers.TryGetValue(attachment, out container);
    }

    public static void ApplyPresentation(Control container, VisualElementAttachmentPresentation presentation)
    {
        container.IsVisible = presentation != VisualElementAttachmentPresentation.Pending;
        container.Opacity = presentation == VisualElementAttachmentPresentation.Normal ? 1d : 0d;
        container.IsHitTestVisible = presentation == VisualElementAttachmentPresentation.Normal;
    }
}

/// <summary>
/// Animates the layout height occupied by the capped attachment viewport while its content remains
/// arranged at the final height. Width follows the parent immediately so wrapping has one stable constraint.
/// </summary>
public sealed class ChatAttachmentSizeTransitionContainer : Decorator
{
    private static TimeSpan Duration => TimeSpan.FromSeconds(0.24d);
    private static readonly CubicEaseOut Easing = new();

    private TopLevel? _topLevel;
    private double _currentHeight;
    private double _fromHeight;
    private double _targetHeight;
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
        // A queued frame cannot be cancelled. Keep its pending flag until that callback runs.
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
        {
            _currentHeight = _targetHeight = 0d;
            return default;
        }

        child.Measure(availableSize);
        var targetSize = child.DesiredSize;
        var targetHeight = ConstrainHeight(targetSize.Height, availableSize.Height);
        if (!_hasArranged || _topLevel is null)
        {
            _currentHeight = _targetHeight = targetHeight;
            _isAnimating = false;
            _startedAt = null;
        }
        else if (Math.Abs(_targetHeight - targetHeight) > 0.01d)
        {
            _fromHeight = _currentHeight;
            _targetHeight = targetHeight;
            _startedAt = null;
            _isAnimating = Math.Abs(_fromHeight - _targetHeight) > 0.01d;
            RequestFrame();
        }

        _currentHeight = ConstrainHeight(_currentHeight, availableSize.Height);
        return new Size(targetSize.Width, _currentHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0d, 0d, finalSize.Width, _targetHeight));
        _hasArranged = true;
        return finalSize;
    }

    private void RequestFrame()
    {
        if (!_isAnimating || _frameRequested || _topLevel is null) return;

        _frameRequested = true;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        _frameRequested = false;
        if (!_isAnimating || _topLevel is null) return;

        if (!IsEffectivelyVisible)
        {
            _currentHeight = _targetHeight;
            _isAnimating = false;
            _startedAt = null;
            InvalidateMeasure();
            return;
        }

        _startedAt ??= time;
        var progress = Math.Clamp((time - _startedAt.Value).TotalSeconds / Duration.TotalSeconds, 0d, 1d);
        var eased = Math.Clamp(Easing.Ease(progress), 0d, 1d);
        _currentHeight = _fromHeight + (_targetHeight - _fromHeight) * eased;
        if (progress >= 1d)
        {
            _currentHeight = _targetHeight;
            _isAnimating = false;
            _startedAt = null;
        }

        InvalidateMeasure();
        RequestFrame();
    }

    private static double ConstrainHeight(double height, double availableHeight) =>
        double.IsInfinity(availableHeight) ? height : Math.Min(height, availableHeight);
}
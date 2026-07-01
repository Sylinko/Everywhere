using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

namespace Everywhere.Views;

[PseudoClasses(IdlePseudoClass, ThinkingPseudoClass, LookingPseudoClass)]
[TemplatePart(Name = EyesContainerPartName, Type = typeof(Control), IsRequired = true)]
[TemplatePart(Name = EyesPartName, Type = typeof(Control), IsRequired = true)]
public class Eva : TemplatedControl
{
    public static readonly StyledProperty<bool> IsIdleAnimationEnabledProperty =
        AvaloniaProperty.Register<Eva, bool>(nameof(IsIdleAnimationEnabled));

    public bool IsIdleAnimationEnabled
    {
        get => GetValue(IsIdleAnimationEnabledProperty);
        set => SetValue(IsIdleAnimationEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> IsThinkingProperty =
        AvaloniaProperty.Register<Eva, bool>(nameof(IsThinking));

    public bool IsThinking
    {
        get => GetValue(IsThinkingProperty);
        set => SetValue(IsThinkingProperty, value);
    }

    private const string EyesContainerPartName = "PART_EyesContainer";
    private const string EyesPartName = "PART_Eyes";

    private const string IdlePseudoClass = ":idle";
    private const string ThinkingPseudoClass = ":thinking";
    private const string LookingPseudoClass = ":looking";

    private Control? _eyesContainer;
    private Control? _eyes;
    private TopLevel? _topLevel;

    public Eva()
    {
        UpdatePseudoClass();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (ReferenceEquals(change.Property, IsIdleAnimationEnabledProperty) ||
            ReferenceEquals(change.Property, IsThinkingProperty))
        {
            UpdatePseudoClass();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _eyesContainer = e.NameScope.Get<Control>(EyesContainerPartName);
        _eyes = e.NameScope.Get<Control>(EyesPartName);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _topLevel = topLevel;
            _topLevel.PointerEntered += HandleTopLevelPointerEntered;
            _topLevel.PointerExited += HandleTopLevelPointerExited;
            _topLevel.PointerMoved += HandleTopLevelPointerMoved;
            UpdatePseudoClass();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_topLevel is not null)
        {
            _topLevel.PointerEntered -= HandleTopLevelPointerEntered;
            _topLevel.PointerExited -= HandleTopLevelPointerExited;
            _topLevel.PointerMoved -= HandleTopLevelPointerMoved;
            _topLevel = null;
        }
    }

    private void HandleTopLevelPointerEntered(object? sender, PointerEventArgs e)
    {
        UpdatePseudoClass();
    }

    private void HandleTopLevelPointerExited(object? sender, PointerEventArgs e)
    {
        UpdatePseudoClass();
    }

    private void HandleTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        LookAt(e);
    }

    /// <summary>
    /// Make Eva look at the given point.
    /// </summary>
    /// <remarks>
    /// Adjusts the margin of the eyes image to simulate eye movement.
    /// Left/right by left: [-2, 2], up/down by bottom: [2, -2].
    /// </remarks>
    private void LookAt(PointerEventArgs e)
    {
        if (_eyes is null) return;

        var point = e.GetPosition(_eyesContainer);
        var centerX = _eyes.Bounds.Width / 2;
        var centerY = _eyes.Bounds.Height / 2;

        // Use a polar coordinate system to limit the eye movement within a circle of radius 2.
        var deltaX = point.X - centerX;
        var deltaY = point.Y - centerY;
        var angle = Math.Atan2(deltaY, deltaX);
        var distance = Math.Min(2, Math.Sqrt(deltaX * deltaX + deltaY * deltaY) / 20); // Scale down the distance
        var offsetX = distance * Math.Cos(angle);
        var offsetY = distance * Math.Sin(angle);

        _eyes.Margin = new Thickness(offsetX, 0, 0, -offsetY);
    }

    private void UpdatePseudoClass()
    {
        PseudoClasses.Set(ThinkingPseudoClass, IsThinking);

        var isIdleAnimationEnabled = IsIdleAnimationEnabled;
        var isLooking = IsPointerOver || _topLevel is { IsPointerOver: true };
        PseudoClasses.Set(LookingPseudoClass, isIdleAnimationEnabled && isLooking);
        PseudoClasses.Set(IdlePseudoClass, isIdleAnimationEnabled && !isLooking);

        // PseudoClasses.Set(":searching", true);
    }
}
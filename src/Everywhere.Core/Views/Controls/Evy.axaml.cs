using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Everywhere.Views;

[PseudoClasses(IdlePseudoClass, ThinkingPseudoClass, LookingPseudoClass)]
[TemplatePart(Name = EyesContainerPartName, Type = typeof(Control), IsRequired = true)]
[TemplatePart(Name = PointerGazeLayerPartName, Type = typeof(Control), IsRequired = true)]
[TemplatePart(Name = EyesPartName, Type = typeof(Control), IsRequired = true)]
public class Evy : TemplatedControl
{
    public static readonly StyledProperty<bool> IsIdleAnimationEnabledProperty =
        AvaloniaProperty.Register<Evy, bool>(nameof(IsIdleAnimationEnabled));

    public bool IsIdleAnimationEnabled
    {
        get => GetValue(IsIdleAnimationEnabledProperty);
        set => SetValue(IsIdleAnimationEnabledProperty, value);
    }

    public static readonly StyledProperty<bool> IsThinkingProperty =
        AvaloniaProperty.Register<Evy, bool>(nameof(IsThinking));

    public bool IsThinking
    {
        get => GetValue(IsThinkingProperty);
        set => SetValue(IsThinkingProperty, value);
    }

    private const string EyesContainerPartName = "PART_EyesContainer";
    private const string PointerGazeLayerPartName = "PART_PointerGazeLayer";
    private const string EyesPartName = "PART_Eyes";

    private const string IdlePseudoClass = ":idle";
    private const string ThinkingPseudoClass = ":thinking";
    private const string LookingPseudoClass = ":looking";
    private const double MaxPointerGazeOffset = 1.6;

    private Control? _eyesContainer;
    private Control? _pointerGazeLayer;
    private TranslateTransform? _pointerGazeTransform;
    private TopLevel? _topLevel;
    private bool _isPointerInWindow;

    public Evy()
    {
        UpdatePseudoClass();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (ReferenceEquals(change.Property, IsIdleAnimationEnabledProperty) ||
            ReferenceEquals(change.Property, IsThinkingProperty))
        {
            if (!IsIdleAnimationEnabled)
            {
                ResetPointerGaze();
            }

            UpdatePseudoClass();
        }
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _eyesContainer = e.NameScope.Get<Control>(EyesContainerPartName);
        _pointerGazeLayer = e.NameScope.Get<Control>(PointerGazeLayerPartName);
        _ = e.NameScope.Get<Control>(EyesPartName);

        // Drive pointer gaze exclusively through a layout-free render transform on a dedicated wrapper layer
        // so mouse tracking never triggers Measure/Arrange feedback or fights idle keyframe animations.
        _pointerGazeTransform = new TranslateTransform
        {
            Transitions =
            [
                new DoubleTransition
                {
                    Property = TranslateTransform.XProperty,
                    Duration = TimeSpan.FromMilliseconds(75),
                    Easing = new CubicEaseOut()
                },
                new DoubleTransition
                {
                    Property = TranslateTransform.YProperty,
                    Duration = TimeSpan.FromMilliseconds(75),
                    Easing = new CubicEaseOut()
                }
            ]
        };
        _pointerGazeLayer.RenderTransform = _pointerGazeTransform;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _topLevel = topLevel;
            _topLevel.PointerEntered += HandleTopLevelPointerEntered;
            _topLevel.PointerExited += HandleTopLevelPointerExited;
            _topLevel.AddHandler(PointerMovedEvent, HandleTopLevelPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
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
            _topLevel.RemoveHandler(PointerMovedEvent, HandleTopLevelPointerMoved);
            _topLevel = null;
        }

        _isPointerInWindow = false;
        ResetPointerGaze();
        UpdatePseudoClass();
    }

    private void HandleTopLevelPointerEntered(object? sender, PointerEventArgs e)
    {
        _isPointerInWindow = true;
        UpdatePseudoClass();
        LookAt(e);
    }

    private void HandleTopLevelPointerExited(object? sender, PointerEventArgs e)
    {
        if (_topLevel is { IsPointerOver: true }) return;

        _isPointerInWindow = false;
        ResetPointerGaze();
        UpdatePseudoClass();
    }

    private void HandleTopLevelPointerMoved(object? sender, PointerEventArgs e)
    {
        _isPointerInWindow = true;
        UpdatePseudoClass();
        LookAt(e);
    }

    /// <summary>
    /// Updates <see cref="Evy"/>'s gaze toward the pointer using a layout-free transform.
    /// </summary>
    private void LookAt(PointerEventArgs e)
    {
        if (!IsIdleAnimationEnabled || _eyesContainer is null) return;
        LookAtEyesContainerPoint(e.GetPosition(_eyesContainer));
    }

    private void LookAtEyesContainerPoint(Point point)
    {
        if (_eyesContainer is null || _pointerGazeTransform is null) return;

        var bounds = _eyesContainer.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var deltaX = point.X - bounds.Width * 0.5;
        var deltaY = point.Y - bounds.Height * 0.5;
        var rawDistance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        if (rawDistance <= 0.001)
        {
            ResetPointerGaze();
            return;
        }

        var scale = Math.Min(MaxPointerGazeOffset, rawDistance / 22.0) / rawDistance;
        _pointerGazeTransform.X = deltaX * scale;
        _pointerGazeTransform.Y = deltaY * scale;
    }

    private void ResetPointerGaze()
    {
        if (_pointerGazeTransform is null) return;
        _pointerGazeTransform.X = 0d;
        _pointerGazeTransform.Y = 0d;
    }

    private void UpdatePseudoClass()
    {
        PseudoClasses.Set(ThinkingPseudoClass, IsThinking);

        var isIdleAnimationEnabled = IsIdleAnimationEnabled;
        var isLooking = _isPointerInWindow || IsPointerOver || _topLevel is { IsPointerOver: true };
        PseudoClasses.Set(LookingPseudoClass, isIdleAnimationEnabled && isLooking);
        PseudoClasses.Set(IdlePseudoClass, isIdleAnimationEnabled && !isLooking);
    }
}

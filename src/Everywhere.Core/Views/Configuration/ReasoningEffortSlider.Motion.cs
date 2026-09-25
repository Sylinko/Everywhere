using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class ReasoningEffortSlider
{
    // Animate scalar decoration properties, never Slider.Value. Native snapping and persistence stay discrete.
    private static readonly StyledProperty<double> IndicatorPositionProperty =
        AvaloniaProperty.Register<ReasoningEffortSlider, double>(nameof(IndicatorPosition));

    private static readonly StyledProperty<double> EdgeStretchProperty =
        AvaloniaProperty.Register<ReasoningEffortSlider, double>(nameof(EdgeStretch));

    private double IndicatorPosition
    {
        get => GetValue(IndicatorPositionProperty);
        set => SetValue(IndicatorPositionProperty, value);
    }

    private double EdgeStretch
    {
        get => GetValue(EdgeStretchProperty);
        set => SetValue(EdgeStretchProperty, value);
    }

    private const double MaximumEdgeStretch = 12;
    private static readonly TimeSpan SelectionDuration = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan ReboundDuration = TimeSpan.FromMilliseconds(220);

    private readonly DoubleTransition _selectionTransition = new()
    {
        Property = IndicatorPositionProperty, Duration = SelectionDuration, Easing = new CubicEaseOut()
    };
    private readonly DoubleTransition _reboundTransition = new()
    {
        Property = EdgeStretchProperty, Duration = ReboundDuration, Easing = EaseOutBack.Soft
    };

    private readonly TranslateTransform _indicatorTranslation = new();
    private readonly MatrixTransform _lineTransform = new();
    private readonly Transitions _motionTransitions = [];

    private double _trackStart;
    private double _trackLength;
    private IPointer? _dragPointer;
    private double _pressPosition;
    private double _interruptedPull;

    private void InitializeMotion()
    {
        _motionTransitions.Add(_selectionTransition);
        Transitions = _motionTransitions;
        AddHandler(PointerPressedEvent, HandlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerMovedEvent, HandleMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, HandleReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, HandleCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void UpdateTrackGeometry()
    {
        if (Track is not { Thumb: { } thumb } track) return;

        var start = thumb.Bounds.Width / 2;
        var length = Math.Max(0, track.Bounds.Width - thumb.Bounds.Width);
        if (_trackStart.IsCloseTo(start) && _trackLength.IsCloseTo(length))
        {
            UpdateDecoration();
            return;
        }

        _trackStart = start;
        _trackLength = length;
        if (_line is not null)
        {
            _line.Width = length;
            _line.Margin = new Thickness(start, 0, 0, 0);
        }

        ResetMotion();
        UpdateDecoration();
    }

    private double GetFraction(double value)
    {
        var fraction = Maximum > Minimum ? (value - Minimum) / (Maximum - Minimum) : 0;
        return IsDirectionReversed ? 1 - fraction : fraction;
    }

    private void MoveIndicator(bool animate)
    {
        if (!animate) _motionTransitions.Remove(_selectionTransition);
        IndicatorPosition = GetFraction(Value) * _trackLength;
        if (!animate) _motionTransitions.Add(_selectionTransition);
        // During normal selection changes this transition stays installed and retargets from its current value.
    }

    private void UpdateDecoration()
    {
        if (_trackLength <= 0) return;
        var leftExtension = Math.Min(0, EdgeStretch);
        var extendedLength = _trackLength + Math.Abs(EdgeStretch);
        var scale = extendedLength / _trackLength;

        // Stretch positions and the line, not circle shapes or input geometry. The opposite endpoint stays fixed.
        _lineTransform.Matrix = new Matrix(scale, 0, 0, 1, leftExtension, 0);
        _indicatorTranslation.X = _trackStart + leftExtension + IndicatorPosition * scale;
        if (_nodes is null) return;
        for (var i = 0; i < _nodes.Children.Count; i++)
        {
            if (_nodes.Children[i].RenderTransform is TranslateTransform translation)
            {
                translation.X = _trackStart + leftExtension + GetFraction(i) * extendedLength - NodeDiameter / 2;
                translation.Y = (Bounds.Height - NodeDiameter) / 2;
            }
        }
    }

    private void HandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsEnabled || !HasMultipleValues || _trackLength <= 0 || _dragPointer is not null ||
            !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // Removing a transition exposes its base target. Sample the displayed value first, then commit it
        // synchronously before the next frame. Selection animation is unaffected by this ownership transfer.
        var displayedStretch = EdgeStretch;
        _motionTransitions.Remove(_reboundTransition);
        EdgeStretch = displayedStretch;
        _interruptedPull = ExpandStretch(displayedStretch);
        _pressPosition = e.GetPosition(this).X;
        _dragPointer = e.Pointer;
    }

    private void HandleMoved(object? sender, PointerEventArgs e)
    {
        if (_dragPointer != e.Pointer) return;
        var position = e.GetPosition(this).X;
        var pull = GetOvershoot(position);
        if (_interruptedPull != 0)
        {
            // Resume from the observed rebound position, with no jump on the first move. Once the user
            // pulls back through the resting endpoint, ordinary absolute edge tracking takes over.
            var resumedPull = _interruptedPull + position - _pressPosition;
            if (Math.Sign(resumedPull) == Math.Sign(_interruptedPull)) pull = resumedPull;
            else _interruptedPull = 0;
        }
        EdgeStretch = CompressPull(pull);
    }

    private double GetOvershoot(double position) =>
        position - Math.Clamp(position, _trackStart, _trackStart + _trackLength);

    /// <summary>
    /// Maps unbounded pointer travel to a signed extension strictly below MaximumEdgeStretch.
    /// The slope starts at one and decreases with distance, producing resistance without a hard stop.
    /// </summary>
    private static double CompressPull(double pull) =>
        MaximumEdgeStretch * (pull / (MaximumEdgeStretch + Math.Abs(pull)));

    /// <summary>
    /// Inverts the resistance curve when a rebound is interrupted, preserving the current visible extension.
    /// Values originate from CompressPull or its non-overshooting transition, so the denominator is positive.
    /// </summary>
    private static double ExpandStretch(double stretch) =>
        MaximumEdgeStretch * stretch / (MaximumEdgeStretch - Math.Abs(stretch));

    private void HandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragPointer == e.Pointer) ReleaseStretch();
    }

    private void HandleCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragPointer == e.Pointer) ReleaseStretch();
    }

    private void ReleaseStretch()
    {
        _dragPointer = null;
        _interruptedPull = 0;
        _motionTransitions.Add(_reboundTransition);
        EdgeStretch = 0;
    }

    private void ResetMotion()
    {
        _dragPointer = null;
        _interruptedPull = 0;
        _motionTransitions.Remove(_reboundTransition);
        EdgeStretch = 0;
        MoveIndicator(animate: false);
    }
}
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Everywhere.Views;

/// <summary>
/// Animates a captured visual element from its screen bounds into the preview image of an attachment.
/// </summary>
public sealed partial class PickVisualElementParticle : Panel, IVisualElementParticle
{
    private const double FlashDurationSeconds = 0.18d;
    private const double LaunchDurationSeconds = 0.22d;
    private const double MorphDurationSeconds = 0.72d;
    private const double MaxFlightDurationSeconds = 1.5d;
    private const double CancelDurationSeconds = 0.2d;
    private const double MaximumCapsuleScale = 1.28d;
    private const double MaximumCancelBlurRadius = 12d;

    private const double SpringStiffness = 120d;
    private const double SpringDamping = 17d;
    private const double MaximumPhysicsFrameSeconds = 1d / 15d;
    private const double PhysicsStepSeconds = 1d / 120d;

    private VisualElementEffectWindow? _owner;
    private readonly BlurEffect _cancelBlurEffect = new();
    private readonly ScaleTransform _endScaleTransform = new();
    private readonly ScaleTransform _shadowScaleTransform = new();

    private Point _startPosition;
    private IParticleTargetTracker? _targetTracker;
    private Size _startSize;
    private Size _endContentSize;
    private Size _endPreviewSize;
    private Vector _endPreviewOffset;
    private Rect _startContentRect;

    private Point _currentPosition;
    private Point _endPosition;
    private double _velocityX;
    private double _velocityY;
    private double _currentSpeed;
    private double _elapsedTimeSeconds;
    private double _flightTimeSeconds;
    private double _morphProgress;
    private double _cancellingTimeSeconds;
    private bool _hasTarget;
    private bool _acceptanceStarted;
    private bool _forceLanding;
    private bool _isCancelling;
    private bool _isCompleted;

    public PickVisualElementParticle()
    {
        InitializeComponent();
        EndContentPresenter.RenderTransform = _endScaleTransform;
        ShadowBorder.RenderTransform = _shadowScaleTransform;
    }

    public void Spawn(
        VisualElementEffectWindow owner,
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize)
    {
        _owner = owner;
        _startPosition = startPosition;
        _targetTracker = targetTracker;
        _startSize = new Size(Math.Max(1d, startSize.Width), Math.Max(1d, startSize.Height));

        StartContentPresenter.Content = startContent;
        EndContentPresenter.Content = endContent;
        EndContentPresenter.UpdateChild();
        PrepareTargetGeometry();

        _currentPosition = _startPosition;
        _endPosition = _startPosition;
        _velocityX = 0d;
        _velocityY = 0d;
        _currentSpeed = 0d;
        _elapsedTimeSeconds = 0d;
        _flightTimeSeconds = 0d;
        _morphProgress = 0d;
        _cancellingTimeSeconds = 0d;
        _hasTarget = false;
        _acceptanceStarted = false;
        _forceLanding = false;
        _isCancelling = false;
        _isCompleted = false;

        Opacity = 1d;
        Effect = null;
        _cancelBlurEffect.Radius = 0d;
        FlashBorder.Opacity = 0d;
        ShadowBorder.Opacity = 0d;
        EndContentPresenter.Opacity = 0d;
        Width = _startSize.Width;
        Height = _startSize.Height;

        TryUpdateEndPosition();
        ApplyVisualState();
    }

    public void Recycle()
    {
        _owner = null;
        _targetTracker = null;
        StartContentPresenter.Content = null;
        EndContentPresenter.Content = null;
        FlashBorder.Opacity = 0d;
        ShadowBorder.BoxShadow = default;
        ShadowBorder.Opacity = 0d;
        EndContentPresenter.Opacity = 0d;
        _endScaleTransform.ScaleX = _endScaleTransform.ScaleY = 1d;
        _shadowScaleTransform.ScaleX = _shadowScaleTransform.ScaleY = 1d;
        _cancelBlurEffect.Radius = 0d;
        Effect = null;
        Opacity = 1d;
    }

    public bool Update(double deltaTimeMs)
    {
        if (_isCompleted || _targetTracker?.IsCompleted is true) return true;
        if (deltaTimeMs <= 0d) return false;

        var realDeltaSeconds = deltaTimeMs / 1000d;
        var previousElapsedTimeSeconds = _elapsedTimeSeconds;
        _elapsedTimeSeconds += realDeltaSeconds;

        if (_isCancelling || _targetTracker?.IsCancelled is true)
        {
            BeginCancellation();
            UpdateCancellation(realDeltaSeconds);
            ApplyVisualState();
            return _isCompleted;
        }

        if (!_acceptanceStarted && _elapsedTimeSeconds >= FlashDurationSeconds)
        {
            _acceptanceStarted = _targetTracker?.BeginAcceptance() is true;
        }

        TryUpdateEndPosition();

        if (!_hasTarget)
        {
            if (_elapsedTimeSeconds >= FlashDurationSeconds + MaxFlightDurationSeconds)
            {
                BeginCancellation();
            }

            ApplyVisualState();
            return false;
        }

        // The flash is a stationary capture confirmation. Physics starts only after it has fully completed.
        var activeDeltaSeconds = previousElapsedTimeSeconds < FlashDurationSeconds ?
            Math.Max(0d, _elapsedTimeSeconds - FlashDurationSeconds) :
            realDeltaSeconds;
        if (activeDeltaSeconds <= 0d)
        {
            ApplyVisualState();
            return false;
        }

        _flightTimeSeconds += activeDeltaSeconds;
        _morphProgress = Math.Clamp(_flightTimeSeconds / MorphDurationSeconds, 0d, 1d);
        UpdateFlight(activeDeltaSeconds);

        var hardLimitReached = _elapsedTimeSeconds >= FlashDurationSeconds + MaxFlightDurationSeconds;
        if (hardLimitReached)
        {
            _morphProgress = 1d;
            _currentPosition = _endPosition;
            _velocityX = 0d;
            _velocityY = 0d;
            _currentSpeed = 0d;
            _forceLanding = true;
        }

        if (_acceptanceStarted &&
            _targetTracker?.IsReadyForHandoff(out var acceptedPointOnScreen) is true &&
            _owner is not null)
        {
            _endPosition = _owner.ScreenPixelToLocal(acceptedPointOnScreen);
            var acceptedDiffX = _currentPosition.X - _endPosition.X;
            var acceptedDiffY = _currentPosition.Y - _endPosition.Y;
            var acceptedPositionSettled = Math.Abs(acceptedDiffX) < 1d &&
                Math.Abs(acceptedDiffY) < 1d &&
                _currentSpeed < 15d;
            if (_forceLanding)
            {
                _currentPosition = _endPosition;
                acceptedPositionSettled = true;
            }

            if (_morphProgress >= 1d && acceptedPositionSettled)
            {
                _currentPosition = _endPosition;
                ApplyVisualState();
                _targetTracker.BeginHandoff();
                return false;
            }
        }

        if (hardLimitReached) BeginCancellation();

        ApplyVisualState();
        return false;
    }

    private void PrepareTargetGeometry()
    {
        EndContentPresenter.Measure(Size.Infinity);
        _endContentSize = EndContentPresenter.DesiredSize;
        _endContentSize = new Size(Math.Max(1d, _endContentSize.Width), Math.Max(1d, _endContentSize.Height));
        EndContentPresenter.Arrange(new Rect(_endContentSize));
        var previewImage = EndContentPresenter.GetVisualDescendants()
            .AsValueEnumerable()
            .OfType<Image>()
            .FirstOrDefault(image => image.Name == "PART_PreviewImage");

        if (previewImage is null ||
            previewImage.Bounds.Width <= 0d ||
            previewImage.Bounds.Height <= 0d ||
            previewImage.TranslatePoint(default, EndContentPresenter) is not { } previewOrigin)
        {
            _endPreviewSize = new Size(16d, 16d);
            _endPreviewOffset = default;
            return;
        }

        _endPreviewSize = previewImage.Bounds.Size;
        _endPreviewOffset = new Vector(
            previewOrigin.X + _endPreviewSize.Width / 2d - _endContentSize.Width / 2d,
            previewOrigin.Y + _endPreviewSize.Height / 2d - _endContentSize.Height / 2d);
    }

    private void TryUpdateEndPosition()
    {
        if (_owner is null || _targetTracker?.TryGetTargetCenterOnScreen(out var endPointOnScreen) is not true)
        {
            return;
        }

        _endPosition = _owner.ScreenPixelToLocal(endPointOnScreen);
        _hasTarget = true;
    }

    private void UpdateFlight(double activeDeltaSeconds)
    {
        var remainingSeconds = Math.Min(activeDeltaSeconds, MaximumPhysicsFrameSeconds);
        var simulatedFlightTimeSeconds = Math.Max(0d, _flightTimeSeconds - remainingSeconds);
        while (remainingSeconds > 0d)
        {
            var stepSeconds = Math.Min(remainingSeconds, PhysicsStepSeconds);
            simulatedFlightTimeSeconds += stepSeconds;
            var launchProgress = SmoothStep(Math.Clamp(simulatedFlightTimeSeconds / LaunchDurationSeconds, 0d, 1d));
            var diffX = _endPosition.X - _currentPosition.X;
            var diffY = _endPosition.Y - _currentPosition.Y;
            var forceX = launchProgress * SpringStiffness * diffX - SpringDamping * _velocityX;
            var forceY = launchProgress * SpringStiffness * diffY - SpringDamping * _velocityY;
            _velocityX += forceX * stepSeconds;
            _velocityY += forceY * stepSeconds;
            _currentPosition = new Point(_currentPosition.X + _velocityX * stepSeconds, _currentPosition.Y + _velocityY * stepSeconds);
            remainingSeconds -= stepSeconds;
        }

        _currentSpeed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);
    }

    private void UpdateCancellation(double realDeltaSeconds)
    {
        _flightTimeSeconds += realDeltaSeconds;
        _morphProgress = Math.Clamp(_flightTimeSeconds / MorphDurationSeconds, 0d, 1d);
        if (_hasTarget) UpdateFlight(realDeltaSeconds);

        _cancellingTimeSeconds += realDeltaSeconds;
        FlashBorder.Opacity = 0d;
        var cancelProgress = Math.Clamp(_cancellingTimeSeconds / CancelDurationSeconds, 0d, 1d);
        Opacity = 1d - SmoothStep(cancelProgress);
        _cancelBlurEffect.Radius = MaximumCancelBlurRadius * SmoothStep(cancelProgress);
        _isCompleted = _cancellingTimeSeconds >= CancelDurationSeconds;
    }

    private void BeginCancellation()
    {
        if (_isCancelling) return;

        _isCancelling = true;
        _targetTracker?.Cancel();
        Effect = _cancelBlurEffect;
        FlashBorder.Opacity = 0d;
    }

    private void ApplyVisualState()
    {
        var morphProgress = SmoothStep(_morphProgress);
        var width = Math.Max(1d, Lerp(_startSize.Width, _endContentSize.Width, morphProgress));
        var height = Math.Max(1d, Lerp(_startSize.Height, _endContentSize.Height, morphProgress));
        var scaleProgress = SmoothStep(Math.Clamp((_morphProgress - 0.22d) / 0.78d, 0d, 1d));
        var capsuleScale = Lerp(MaximumCapsuleScale, 1d, scaleProgress);
        var targetPreviewWidth = _endPreviewSize.Width * capsuleScale;
        var targetPreviewHeight = _endPreviewSize.Height * capsuleScale;
        var previewWidth = Math.Max(1d, Lerp(_startSize.Width, targetPreviewWidth, morphProgress));
        var previewHeight = Math.Max(1d, Lerp(_startSize.Height, targetPreviewHeight, morphProgress));
        var previewCenter = new Point(
            width / 2d + _endPreviewOffset.X * capsuleScale * morphProgress,
            height / 2d + _endPreviewOffset.Y * capsuleScale * morphProgress);
        _startContentRect = new Rect(
            previewCenter.X - previewWidth / 2d,
            previewCenter.Y - previewHeight / 2d,
            previewWidth,
            previewHeight);

        Width = width;
        Height = height;
        InvalidateArrange();
        Canvas.SetLeft(this, _currentPosition.X - width / 2d);
        Canvas.SetTop(this, _currentPosition.Y - height / 2d);

        var capsuleProgress = SmoothStep(Math.Clamp((_morphProgress - 0.28d) / 0.32d, 0d, 1d));
        ShadowBorder.Opacity = capsuleProgress;
        EndContentPresenter.Opacity = capsuleProgress;
        _endScaleTransform.ScaleX = _endScaleTransform.ScaleY = capsuleScale;
        _shadowScaleTransform.ScaleX = _shadowScaleTransform.ScaleY = capsuleScale;

        var lift = Math.Sin(Math.PI * _morphProgress);
        ShadowBorder.BoxShadow = lift <= 0.001d ?
            default :
            new BoxShadows(
                new BoxShadow
                {
                    OffsetY = 2d + 8d * lift,
                    Blur = 4d + 18d * lift,
                    Spread = lift,
                    Color = Color.FromArgb((byte)Math.Round(64d * lift), 0, 0, 0)
                });

        if (!_isCancelling)
        {
            var flashProgress = Math.Clamp(_elapsedTimeSeconds / FlashDurationSeconds, 0d, 1d);
            FlashBorder.Opacity = Math.Sin(flashProgress * Math.PI) * 0.5d;
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Keep the cached capsule at its endpoint size; only the screenshot's layout changes in flight.
        ShadowBorder.Measure(_endContentSize);
        EndContentPresenter.Measure(_endContentSize);
        StartContentBorder.Measure(_startContentRect.Size);
        FlashBorder.Measure(_startContentRect.Size);

        return new Size(
            double.IsInfinity(availableSize.Width) ? Math.Max(_startContentRect.Right, _endContentSize.Width) : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? Math.Max(_startContentRect.Bottom, _endContentSize.Height) : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var targetRect = new Rect(
            (finalSize.Width - _endContentSize.Width) / 2d,
            (finalSize.Height - _endContentSize.Height) / 2d,
            _endContentSize.Width,
            _endContentSize.Height);
        ShadowBorder.Arrange(targetRect);
        EndContentPresenter.Arrange(targetRect);
        StartContentBorder.Arrange(_startContentRect);
        FlashBorder.Arrange(_startContentRect);
        return finalSize;
    }

    private static double Lerp(double start, double end, double progress) =>
        start + (end - start) * progress;

    private static double SmoothStep(double value) => value * value * (3d - 2d * value);
}
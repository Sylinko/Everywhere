using Avalonia.Controls;
using Avalonia.Media;

namespace Everywhere.Views;

/// <summary>
/// Control-based particle with spring dynamics for position and time-based easing for visual morphing.
/// Single particles morph into their target control, while multi-particles maintain their size and just use physics.
/// Designed to be managed by a bounded object pool using Spawn / Recycle mechanics.
/// </summary>
public sealed partial class PickVisualElementParticle : VisualElementParticle
{
    private const double MorphDurationSec = 0.55;
    private const double MaxTimeoutSec = 0.7;

    private const double SpringStiffness = 180.0;
    private const double SpringDamping = 22.0;

    private const double MaxShadowBlur = 24.0;
    private const double MaxShadowOffset = 25.0;
    private const double BaseShadowBlur = 4.0;
    private const double BaseShadowOffset = 2.0;

    private readonly DropShadowEffect _dropShadowEffect;

    private VisualElementEffectWindow? _owner;
    private Point _startPosition;
    private IParticleTargetTracker? _targetTracker;
    private Size _startSize;

    private double _morphProgress;
    private double _velocityX;
    private double _velocityY;
    private Point _currentPosition;
    private double _currentSpeed;
    private Point _endPosition;
    private Size _endSize;
    private double _elapsedTimeSeconds;
    private bool _isCompleted;
    private double _cancellingTimeSeconds;
    private BlurEffect? _cancellingBlurEffect;

    public PickVisualElementParticle()
    {
        InitializeComponent();

        BackgroundBorder.Effect = _dropShadowEffect = new DropShadowEffect
        {
            Color = Colors.Black
        };
    }

    /// <summary>
    /// Spawns (initializes or re-initializes) the particle with the provided physics and content logic.
    /// Acts as the surrogate constructor when dequeuing from an object pool.
    /// </summary>
    public override void Spawn(
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize)
    {
        _owner = TopLevel.GetTopLevel(this).NotNull<VisualElementEffectWindow>();

        _startPosition = startPosition;
        _targetTracker = targetTracker;
        StartContentPresenter.Content = startContent;
        EndContentPresenter.Content = endContent;
        _startSize = startSize;

        Width = _startSize.Width;
        Height = _startSize.Height;
        
        _morphProgress = 0d;
        _velocityX = 0d;
        _velocityY = 0d;
        _elapsedTimeSeconds = 0d;
        _isCompleted = false;
        _cancellingTimeSeconds = 0d;
        Effect = _cancellingBlurEffect = null;
        Opacity = 1d;

        // Force a synchronous measure to establish the end size representation before flight
        if (endContent is not null)
        {
            EndContentPresenter.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _endSize = EndContentPresenter.DesiredSize;
            EndContentPresenter.Width = _endSize.Width;
            EndContentPresenter.Height = _endSize.Height;
        }

        InitializeFlightDynamics();
    }

    /// <summary>
    /// Recycles the particle, severing strong references to expensive UI and image contents, 
    /// allowing it to rest idly in the memory pool without preventing GC of transient bounds.
    /// </summary>
    public override void Recycle()
    {
        _targetTracker = null;
        StartContentPresenter.Content = null;
        EndContentPresenter.Content = null;
    }

    private void InitializeFlightDynamics()
    {
        _currentPosition = _startPosition;
        UpdateEndPosition();

        var dx = _endPosition.X - _startPosition.X;
        var dy = _endPosition.Y - _startPosition.Y;

        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1d) dist = 1d;

        var nx = -dy / dist;
        var ny = dx / dist;

        var lateralSpeed = Random.Shared.NextDouble() * 3000d - 1500d;
        var forwardSpeed = Random.Shared.NextDouble() * 500d;

        _velocityX = nx * lateralSpeed + (dx / dist) * forwardSpeed;
        _velocityY = ny * lateralSpeed + (dy / dist) * forwardSpeed;

        ApplyVisualState();
    }

    private void UpdateEndPosition()
    {
        if (_owner is not null && _targetTracker?.TryGetTargetCenterOnScreen(out var endPointOnScreen) is true)
        {
            _endPosition = _owner.ScreenPixelToLocal(endPointOnScreen);
        }
    }

    public override bool Update(double deltaTimeMs)
    {
        if (_isCompleted) return true;
        if (deltaTimeMs <= 0) return false;

        var deltaSeconds = deltaTimeMs / 1000d;
        _elapsedTimeSeconds += deltaSeconds;

        if (_cancellingBlurEffect is not null)
        {
            // do cancelling animation
            _cancellingTimeSeconds += deltaSeconds;
            _cancellingBlurEffect.Radius = _cancellingTimeSeconds * 23d;
            Opacity = Math.Max(1.0 - _cancellingTimeSeconds / 0.6d, 0d);
        }
        else if (_targetTracker?.IsCancelled is true)
        {
            // should cancel
            Effect = _cancellingBlurEffect = new BlurEffect();
        }

        UpdateEndPosition();
        var diffX = _currentPosition.X - _endPosition.X;
        var diffY = _currentPosition.Y - _endPosition.Y;
        var forceX = -SpringStiffness * diffX - SpringDamping * _velocityX;
        var forceY = -SpringStiffness * diffY - SpringDamping * _velocityY;
        _velocityX += forceX * deltaSeconds;
        _velocityY += forceY * deltaSeconds;

        _currentPosition = new Point(_currentPosition.X + _velocityX * deltaSeconds, _currentPosition.Y + _velocityY * deltaSeconds);
        _currentSpeed = Math.Sqrt(_velocityX * _velocityX + _velocityY * _velocityY);

        var positionSettled = Math.Abs(diffX) < 1d && Math.Abs(diffY) < 1d && _currentSpeed < 15d;
        _morphProgress = Math.Clamp(_elapsedTimeSeconds / MorphDurationSec, 0d, 1d);

        if (_cancellingBlurEffect is not null && _cancellingTimeSeconds > 0.6d ||
            _cancellingBlurEffect is null && (positionSettled && _morphProgress >= 1d || _elapsedTimeSeconds > MaxTimeoutSec))
        {
            _isCompleted = true;
            _targetTracker?.OnParticleCompleted();
        }

        ApplyVisualState();
        return _isCompleted;
    }

    private void ApplyVisualState()
    {
        var t = CubicEaseOut(_morphProgress);
        var elevation = Math.Sin(_morphProgress * Math.PI);

        var baseWidth = _startSize.Width + (_endSize.Width - _startSize.Width) * t;
        var baseHeight = _startSize.Height + (_endSize.Height - _startSize.Height) * t;

        var finalWidth = Math.Max(1d, baseWidth);
        var finalHeight = Math.Max(1d, baseHeight);

        Width = finalWidth;
        Height = finalHeight;

        Canvas.SetLeft(this, _currentPosition.X - finalWidth / 2d);
        Canvas.SetTop(this, _currentPosition.Y - finalHeight / 2d);

        var radius = 8d - (4d * t);
        RootBorder.CornerRadius = BackgroundBorder.CornerRadius = StartContentBorder.CornerRadius = new CornerRadius(Math.Max(3d, radius));

        var currentShadowBlur = BaseShadowBlur + (MaxShadowBlur * elevation);
        var currentShadowOffsetY = BaseShadowOffset + (MaxShadowOffset * elevation);
        var shadowOpacity = 0.6 * elevation;

        _dropShadowEffect.BlurRadius = currentShadowBlur;
        _dropShadowEffect.OffsetY = currentShadowOffsetY;
        _dropShadowEffect.Opacity = shadowOpacity;

        StartContentPresenter.Opacity = Math.Min(2d - 2d * t, 1d);
        EndContentPresenter.Opacity = t;
    }

    private static double CubicEaseOut(double x)
    {
        var t = x - 1d;
        return 1d + t * t * t;
    }
}
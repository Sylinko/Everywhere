using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Everywhere.Views;

/// <summary>
/// A canvas control that renders a confetti animation effect.
/// Modified from https://github.com/tootster/WPF-Confetti-Effect/blob/main/ConfettiEffect.cs
/// </summary>
public class ConfettiEffect : Canvas
{
    /// <summary>
    /// Represents a single piece of confetti.
    /// </summary>
    private class ConfettiParticle
    {
        // The shape to be rendered.
        public required Path Shape { get; init; }

        // Initial horizontal position.
        public required double InitialX { get; init; }

        // Initial vertical position.
        public required double InitialY { get; init; }

        // Horizontal velocity.
        public required double VelocityX { get; init; }

        // Vertical velocity.
        public required double VelocityY { get; init; }

        // Duration of the particle's life in seconds.
        public required double LifeTime { get; init; }

        // Amplitude of the sine wave for horizontal sway.
        public required double SinWaveAmplitude { get; init; }

        // Frequency of the sine wave for horizontal sway.
        public required double SinWaveFrequency { get; init; }

        // Phase offset of the sine wave.
        public required double SinWaveOffset { get; init; }

        // Brush for the front face of the confetti.
        public required IBrush FrontFill { get; init; }

        // Brush for the back face of the confetti (usually darker).
        public required IBrush BackFill { get; init; }

        // Speed at which the particle tumbles (for the 3D effect).
        public required double TumbleSpeed { get; init; }
    }

    private readonly Random _random = new();
    private readonly List<ConfettiParticle> _particles = [];
    private readonly CubicEaseIn _easeIn = new();
    private TimeSpan _startTime;
    private bool _isRunning;
    private int _runToken;

    /// <summary>
    /// A predefined list of colors for the confetti particles.
    /// </summary>
    private static readonly IReadOnlyList<Color> PossibleColors = new[]
    {
        Color.Parse("#1E90FF"), // Bright Blue
        Color.Parse("#32CD32"), // Lime Green
        Color.Parse("#FFD700"), // Gold
        Color.Parse("#FF69B4"), // Hot Pink
        Color.Parse("#8A2BE2"), // Blue Violet
        Color.Parse("#87CEFA"), // Light Sky Blue
        Color.Parse("#FFB90F"), // Dark Golden Rod
        Color.Parse("#EE82EE"), // Violet
        Color.Parse("#98FB98"), // Pale Green
        Color.Parse("#B0C4DE"), // Light Steel Blue
        Color.Parse("#F4A460"), // Sandy Brown
        Color.Parse("#D2691E"), // Chocolate
        Color.Parse("#DC143C") // Crimson
    };

    #region Properties

    /// <summary>
    /// Gets or sets the total number of confetti particles.
    /// </summary>
    public int ConfettiCount { get; set; } = 400;

    /// <summary>
    /// Gets or sets the minimum initial velocity of a particle.
    /// </summary>
    public double MinInitialVelocity { get; set; } = 300;

    /// <summary>
    /// Gets or sets the maximum initial velocity of a particle.
    /// </summary>
    public double MaxInitialVelocity { get; set; } = 600;

    /// <summary>
    /// Gets or sets the downward acceleration due to gravity.
    /// </summary>
    public double Gravity { get; set; } = 980;

    /// <summary>
    /// Gets or sets the lifetime of particles in seconds.
    /// </summary>
    public double ParticleLifeTime { get; set; } = 4.5;

    /// <summary>
    /// Gets or sets the minimum size of a confetti particle.
    /// </summary>
    public double MinConfettiSize { get; set; } = 5.5;

    /// <summary>
    /// Gets or sets the maximum size of a confetti particle.
    /// </summary>
    public double MaxConfettiSize { get; set; } = 16.5;

    /// <summary>
    /// Gets or sets the minimum amplitude of the sine wave for horizontal movement.
    /// </summary>
    public double MinAmplitude { get; set; } = 10;

    /// <summary>
    /// Gets or sets the maximum amplitude of the sine wave for horizontal movement.
    /// </summary>
    public double MaxAmplitude { get; set; } = 25;

    /// <summary>
    /// Gets or sets the minimum frequency of the sine wave for horizontal movement.
    /// </summary>
    public double MinFrequency { get; set; } = 0.3;

    /// <summary>
    /// Gets or sets the maximum frequency of the sine wave for horizontal movement.
    /// </summary>
    public double MaxFrequency { get; set; } = 0.6;

    #endregion

    static ConfettiEffect()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<ConfettiEffect>(false);
    }

    /// <summary>
    /// Starts the confetti animation.
    /// </summary>
    public void Start()
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;
        var runToken = ++_runToken;
        Children.Clear();
        _particles.Clear();

        // Create and add all confetti particles to the canvas.
        for (var i = 0; i < ConfettiCount; i++)
        {
            var particle = CreateConfettiParticle();
            _particles.Add(particle);
            Children.Add(particle.Shape);
        }

        _startTime = TimeSpan.Zero;
        // Request the first animation frame.
        TopLevel.GetTopLevel(this)?.RequestAnimationFrame(time => OnFrame(time, runToken));
    }

    /// <summary>
    /// Stops the confetti animation and clears active particles.
    /// </summary>
    public void Stop()
    {
        _isRunning = false;
        _runToken++;
        _particles.Clear();
        Children.Clear();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Stop();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Called on each frame to update the animation.
    /// </summary>
    /// <param name="time">The current time provided by the animation system.</param>
    /// <param name="runToken">The run generation captured when this animation loop started.</param>
    private void OnFrame(TimeSpan time, int runToken)
    {
        if (!_isRunning || runToken != _runToken) return;

        // Initialize start time on the first frame.
        if (_startTime == TimeSpan.Zero)
        {
            _startTime = time;
        }

        var elapsedSeconds = (time - _startTime).TotalSeconds;
        var allFinished = true;

        foreach (var p in _particles)
        {
            // Skip particles that have exceeded their lifetime.
            if (elapsedSeconds > p.LifeTime)
            {
                p.Shape.IsVisible = false;
                continue;
            }

            allFinished = false; // At least one particle is still active.

            // Calculate position with initial velocity and gravity.
            var y = p.InitialY + (p.VelocityY * elapsedSeconds) + (0.5 * Gravity * elapsedSeconds * elapsedSeconds);
            // Add a sine wave for horizontal swaying motion.
            var xSway = p.SinWaveAmplitude * Math.Sin(p.SinWaveOffset + elapsedSeconds * p.SinWaveFrequency);
            var x = p.InitialX + (p.VelocityX * elapsedSeconds) + xSway;

            // Fade out the particle as it nears the end of its life.
            var progress = elapsedSeconds / p.LifeTime;
            p.Shape.Opacity = 1.0 - _easeIn.Ease(progress);

            // --- Pseudo 3D Effect ---
            // 2D rotation (spin around Z-axis).
            var rotationAngle = elapsedSeconds * 360;
            // 3D tumble (rotation around Y-axis).
            var tumbleAngle = elapsedSeconds * p.TumbleSpeed;
            var cosTumble = Math.Cos(tumbleAngle);

            // Switch between front and backfill based on the tumble angle.
            p.Shape.Fill = cosTumble >= 0 ? p.FrontFill : p.BackFill;

            // Combine transforms: scale, rotate, then translate.
            // The RenderTransformOrigin is set to the center, so scale and rotate apply correctly.
            var transform = new TransformGroup();
            // 1. Scale horizontally to simulate the 3D tumble.
            transform.Children.Add(new ScaleTransform(Math.Abs(cosTumble), 1));
            // 2. Apply the 2D spin.
            transform.Children.Add(new RotateTransform(rotationAngle));
            // 3. Move the particle to its final position.
            transform.Children.Add(new TranslateTransform(x, y));

            p.Shape.RenderTransform = transform;
        }

        if (allFinished)
        {
            // Stop the animation and clear the canvas if all particles are done.
            Stop();
            Children.Clear();
        }
        else
        {
            // Request the next animation frame.
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(nextTime => OnFrame(nextTime, runToken));
        }
    }

    /// <summary>
    /// Creates a single confetti particle with random properties.
    /// </summary>
    /// <returns>A new ConfettiParticle instance.</returns>
    private ConfettiParticle CreateConfettiParticle()
    {
        // Generate random dimensions for the rectangle to look like a paper strip.
        var width = _random.NextDouble() * (MaxConfettiSize - MinConfettiSize) + MinConfettiSize;
        var height = _random.NextDouble() * (width * 0.6) + (width * 0.2); // Height is 20% to 80% of width.

        var color = PossibleColors[_random.Next(PossibleColors.Count)];
        var frontFill = new SolidColorBrush(color);
        // Create a darker color for the back face.
        var backFill = new SolidColorBrush(
            Color.FromRgb(
                (byte)(color.R * 0.6),
                (byte)(color.G * 0.6),
                (byte)(color.B * 0.6)));

        var shape = new Path
        {
            // Use RectangleGeometry to create a paper strip shape.
            Data = new RectangleGeometry(new Rect(0, 0, width, height)),
            Fill = frontFill, // Start with the front color.
            // Set the transform origin to the center for proper rotation and scaling.
            RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative)
        };

        var initialVelocity = _random.NextDouble() * (MaxInitialVelocity - MinInitialVelocity) + MinInitialVelocity;

        double initialX;
        double initialY;
        double angle;

        // A margin to ensure particles start completely off-screen.
        var margin = MaxConfettiSize * 2;

        // Randomly decide the starting edge: 0 for left, 1 for right, 2 for bottom.
        var edge = _random.Next(3);

        if (edge == 0) // Start from the left, off-screen.
        {
            initialX = -margin;
            initialY = _random.NextDouble() * Bounds.Height;
            // Angle towards the right (0 to 90 degrees).
            angle = _random.NextDouble() * (Math.PI / 2);
        }
        else if (edge == 1) // Start from the right, off-screen.
        {
            initialX = Bounds.Width + margin;
            initialY = _random.NextDouble() * Bounds.Height;
            // Angle towards the left (90 to 180 degrees).
            angle = _random.NextDouble() * (Math.PI / 2) + (Math.PI / 2);
        }
        else // Start from the bottom, off-screen.
        {
            initialX = _random.NextDouble() * Bounds.Width;
            initialY = Bounds.Height + margin;
            // Angle upwards (0 to 180 degrees).
            angle = _random.NextDouble() * Math.PI;
        }

        // We use -Sin(angle) for Y because the Y-axis points downwards in the coordinate system.
        var velocityX = initialVelocity * Math.Cos(angle);
        var velocityY = -initialVelocity * Math.Sin(angle);

        return new ConfettiParticle
        {
            Shape = shape,
            LifeTime = ParticleLifeTime,
            InitialX = initialX,
            InitialY = initialY,
            VelocityX = velocityX,
            VelocityY = velocityY,
            SinWaveAmplitude = _random.NextDouble() * (MaxAmplitude - MinAmplitude) + MinAmplitude,
            SinWaveFrequency = _random.NextDouble() * (MaxFrequency - MinFrequency) + MinFrequency,
            SinWaveOffset = _random.NextDouble() * Math.PI * 2,
            FrontFill = frontFill,
            BackFill = backFill,
            TumbleSpeed = _random.NextDouble() * 10 + 5 // Random tumble speed.
        };
    }
}
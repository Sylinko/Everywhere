using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// Theme-aware ambient Skia light field background shared across all onboarding scenes.
/// Gently drifts its horizon radiance while pausing automatically when detached, hidden, or minimized.
/// </summary>
public sealed class WelcomeAmbientBackground : Control
{
    /// <summary>
    /// Defines the <see cref="BaseColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> BaseColorProperty =
        AvaloniaProperty.Register<WelcomeAmbientBackground, Color>(nameof(BaseColor), Color.FromArgb(236, 17, 19, 25));

    /// <summary>
    /// Defines the <see cref="CoolColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> CoolColorProperty =
        AvaloniaProperty.Register<WelcomeAmbientBackground, Color>(nameof(CoolColor), Color.FromRgb(22, 40, 79));

    /// <summary>
    /// Defines the <see cref="AccentColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> AccentColorProperty =
        AvaloniaProperty.Register<WelcomeAmbientBackground, Color>(nameof(AccentColor), Color.FromRgb(32, 45, 94));

    /// <summary>
    /// Defines the <see cref="WarmColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> WarmColorProperty =
        AvaloniaProperty.Register<WelcomeAmbientBackground, Color>(nameof(WarmColor), Color.FromRgb(23, 27, 38));

    public Color BaseColor
    {
        get => GetValue(BaseColorProperty);
        set => SetValue(BaseColorProperty, value);
    }

    public Color CoolColor
    {
        get => GetValue(CoolColorProperty);
        set => SetValue(CoolColorProperty, value);
    }

    public Color AccentColor
    {
        get => GetValue(AccentColorProperty);
        set => SetValue(AccentColorProperty, value);
    }

    public Color WarmColor
    {
        get => GetValue(WarmColorProperty);
        set => SetValue(WarmColorProperty, value);
    }

    private const string Shader = """
                                  uniform float2 size;
                                  uniform float4 baseColor;
                                  uniform float4 coolColor;
                                  uniform float4 accentColor;
                                  uniform float4 warmColor;
                                  uniform float opacity;
                                  uniform float uTime;

                                  float field(float2 p, float2 center, float2 radius) {
                                      float2 d = (p - center) / radius;
                                      return exp(-1.90 * dot(d, d));
                                  }

                                  half4 main(float2 p) {
                                      float2 uv = p / max(size, float2(1.0, 1.0));
                                      float2 topDrift = float2(0.06 * sin(uTime * 0.22), 0.03 * cos(uTime * 0.17));
                                      float2 rightDrift = float2(0.04 * cos(uTime * 0.19), 0.05 * sin(uTime * 0.24));

                                      float3 c = baseColor.rgb;
                                      c = mix(c, coolColor.rgb, 0.78 * field(uv, float2(0.50, -0.02) + topDrift, float2(0.60, 0.44)));
                                      c = mix(c, accentColor.rgb, 0.56 * field(uv, float2(0.84, 0.34) + rightDrift, float2(0.42, 0.46)));
                                      c = mix(c, warmColor.rgb, 0.50 * field(uv, float2(0.18, 0.88) - topDrift * 0.5, float2(0.50, 0.38)));

                                      float vignette = smoothstep(0.0, 1.15, dot(uv - float2(0.5, 0.38), uv - float2(0.5, 0.38)));
                                      c = mix(c, baseColor.rgb, vignette * 0.28);
                                      float grain = fract(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453) - 0.5;
                                      c = clamp(c + grain / 255.0, 0.0, 1.0);
                                      float alpha = clamp(baseColor.a * opacity, 0.0, 1.0);
                                      return half4(c * alpha, alpha);
                                  }
                                  """;

    private static readonly Lazy<SKRuntimeEffect> ShaderEffect = new(() =>
        SKRuntimeEffect.CreateShader(Shader, out var error) ??
        throw new InvalidOperationException("Failed to compile welcome ambient shader: " + error));

    private readonly OobeShaderClock _clock;

    static WelcomeAmbientBackground()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<WelcomeAmbientBackground>(false);
        AffectsRender<WelcomeAmbientBackground>(BaseColorProperty, CoolColorProperty, AccentColorProperty, WarmColorProperty);
    }

    public WelcomeAmbientBackground()
    {
        _clock = new OobeShaderClock(this);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.Attach();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _clock.Detach();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == OpacityProperty)
        {
            _clock.Synchronize();
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || Opacity <= 0.001) return;
        context.Custom(
            new AmbientDrawOperation(
                new Rect(Bounds.Size),
                BaseColor,
                CoolColor,
                AccentColor,
                WarmColor,
                _clock.ElapsedSeconds));
    }

    private sealed class AmbientDrawOperation(Rect bounds, Color baseColor, Color cool, Color accent, Color warm, double time)
        : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var effect = ShaderEffect.Value;
            using var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["size"] = new[] { (float)Bounds.Width, (float)Bounds.Height };
            uniforms["baseColor"] = OobeShaderClock.ColorComponents(baseColor);
            uniforms["coolColor"] = OobeShaderClock.ColorComponents(cool);
            uniforms["accentColor"] = OobeShaderClock.ColorComponents(accent);
            uniforms["warmColor"] = OobeShaderClock.ColorComponents(warm);
            uniforms["opacity"] = (float)lease.CurrentOpacity;
            uniforms["uTime"] = (float)time;
            using var children = new SKRuntimeEffectChildren(effect);
            using var shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint();
            paint.Shader = shader;
            paint.IsDither = true;
            var canvas = lease.SkCanvas;
            canvas.Save();
            try
            {
                canvas.Translate((float)Bounds.X, (float)Bounds.Y);
                canvas.DrawRect(0, 0, (float)Bounds.Width, (float)Bounds.Height, paint);
            }
            finally
            {
                canvas.Restore();
            }
        }

        public bool HitTest(Point p) => false;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
    }
}

/// <summary>
/// Shared UI-thread frame clock for OOBE Skia shader controls that pauses automatically when hidden or minimized.
/// </summary>
internal sealed class OobeShaderClock(Control owner, Action<double>? onStep = null, Func<bool>? shouldRun = null)
{
    public double ElapsedSeconds { get; private set; }

    private readonly List<Visual> _observedAncestors = [];
    private TopLevel? _topLevel;
    private Window? _window;
    private bool _isRunning;
    private int _generation;
    private TimeSpan? _lastFrameTime;

    public static float[] ColorComponents(Color color) => [color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f];

    public void Attach()
    {
        Detach();
        _topLevel = TopLevel.GetTopLevel(owner);
        _window = _topLevel as Window;
        if (_window is not null) _window.PropertyChanged += HandleWindowChanged;
        foreach (var ancestor in owner.GetVisualAncestors())
        {
            if (ReferenceEquals(ancestor, _window)) continue;
            ancestor.PropertyChanged += HandleAncestorChanged;
            _observedAncestors.Add(ancestor);
        }

        Synchronize();
    }

    public void Detach()
    {
        if (_window is not null) _window.PropertyChanged -= HandleWindowChanged;
        foreach (var ancestor in _observedAncestors)
        {
            ancestor.PropertyChanged -= HandleAncestorChanged;
        }

        _observedAncestors.Clear();
        _window = null;
        _topLevel = null;
        _isRunning = false;
        _lastFrameTime = null;
        _generation++;
    }

    public void Synchronize()
    {
        if (!ShouldAnimate())
        {
            _isRunning = false;
            _lastFrameTime = null;
            _generation++;
            return;
        }

        if (_isRunning) return;
        _isRunning = true;
        _lastFrameTime = null;
        var generation = ++_generation;
        _topLevel?.RequestAnimationFrame(timestamp => OnFrame(timestamp, generation));
    }

    private void HandleAncestorChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Visual.IsVisibleProperty)
        {
            Synchronize();
        }
    }

    private void HandleWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property == Window.WindowStateProperty || args.Property == Visual.IsVisibleProperty)
        {
            Synchronize();
        }
    }

    private bool ShouldAnimate() =>
        _topLevel is not null &&
        owner.IsEffectivelyVisible &&
        owner.Opacity > 0.001 &&
        (_window is null || _window is { IsVisible: true, WindowState: not WindowState.Minimized }) &&
        (shouldRun is null || shouldRun());

    private void OnFrame(TimeSpan timestamp, int generation)
    {
        if (generation != _generation) return;
        if (!ShouldAnimate())
        {
            _isRunning = false;
            _lastFrameTime = null;
            _generation++;
            return;
        }

        var dt = 0.0;
        if (_lastFrameTime is { } previous)
        {
            var delta = timestamp - previous;
            if (delta > TimeSpan.Zero)
            {
                dt = Math.Min(delta.TotalSeconds, 0.1);
                ElapsedSeconds += dt;
            }
        }

        _lastFrameTime = timestamp;
        onStep?.Invoke(dt);
        owner.InvalidateVisual();
        if (generation == _generation)
        {
            _topLevel?.RequestAnimationFrame(nextTimestamp => OnFrame(nextTimestamp, generation));
        }
    }
}

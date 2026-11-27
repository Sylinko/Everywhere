using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Skia;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// A content host that overlays a resolution-independent animated glow inside its rounded edge.
/// The card background and ordinary border remain the responsibility of the hosted content.
/// </summary>
public sealed class GlowBorder : ContentControl
{
    public static readonly StyledProperty<Color> GlowColorProperty =
        AvaloniaProperty.Register<GlowBorder, Color>(nameof(GlowColor), Colors.Transparent);

    public static readonly StyledProperty<double> GlowOpacityProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(GlowOpacity));

    public static readonly StyledProperty<double> AnimationRateProperty =
        AvaloniaProperty.Register<GlowBorder, double>(nameof(AnimationRate), 1);

    public Color GlowColor
    {
        get => GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    public double GlowOpacity
    {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    /// <summary>
    /// Gets or sets the multiplier applied to shader time. A value of zero preserves the current
    /// glow shape without requesting further animation frames.
    /// </summary>
    public double AnimationRate
    {
        get => GetValue(AnimationRateProperty);
        set => SetValue(AnimationRateProperty, value);
    }
}

/// <summary>
/// Shader overlay used by <see cref="GlowBorder"/>. It requests frames only while visible and
/// its effective opacity is above a small threshold.
/// </summary>
public sealed class GlowBorderOverlay : Control
{
    public Color GlowColor
    {
        get => GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double GlowOpacity
    {
        get => GetValue(GlowOpacityProperty);
        set => SetValue(GlowOpacityProperty, value);
    }

    /// <summary>
    /// Gets or sets the multiplier applied to elapsed animation time. Transitions may gradually
    /// approach zero to decelerate the highlight before it becomes a static inner glow.
    /// </summary>
    public double AnimationRate
    {
        get => GetValue(AnimationRateProperty);
        set => SetValue(AnimationRateProperty, value);
    }

    public static readonly StyledProperty<Color> GlowColorProperty = GlowBorder.GlowColorProperty.AddOwner<GlowBorderOverlay>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = TemplatedControl.CornerRadiusProperty.AddOwner<GlowBorderOverlay>();
    public static readonly StyledProperty<double> GlowOpacityProperty = GlowBorder.GlowOpacityProperty.AddOwner<GlowBorderOverlay>();
    public static readonly StyledProperty<double> AnimationRateProperty = GlowBorder.AnimationRateProperty.AddOwner<GlowBorderOverlay>();

    private static readonly Lazy<SKRuntimeEffect> ShaderEffect = new(CreateEffect);

    private readonly ShaderVisualHost _host;

    /// <summary>
    /// Creates an overlay whose glow animates independently of the UI thread.
    /// </summary>
    public GlowBorderOverlay()
    {
        _host = new ShaderVisualHost(this, static () => new GlowHandler(), SendState);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == GlowColorProperty || change.Property == CornerRadiusProperty ||
            change.Property == GlowOpacityProperty || change.Property == AnimationRateProperty)
        {
            SendState();
        }
    }

    private void SendState()
    {
        if (_host.IsAttached) _host.Send(new GlowState(GlowColor, CornerRadius.TopLeft, GlowOpacity, AnimationRate));
    }

    private static SKRuntimeEffect CreateEffect()
    {
        return SKRuntimeEffect.CreateShader(ShaderSource, out var errorText)
            ?? throw new InvalidOperationException("Failed to compile GlowBorder shader: " + errorText);
    }

    private sealed record GlowState(Color Color, double Radius, double Opacity, double Rate);

    private sealed class GlowHandler : SkiaShaderVisualHandler
    {
        protected override bool IsAnimating => _state is { Rate: > 0.001, Opacity: > 0.001, Color.A: > 0 };

        private GlowState? _state;
        private double _time;
        private Vector _cachedSize;
        private bool _hasCachedShader;

        protected override void OnStateChanged(object message)
        {
            _state = (GlowState)message;
            _hasCachedShader = false;
        }

        protected override void Advance(double dt)
        {
            if (_state is null) return;
            _time += dt * _state.Rate;
            _hasCachedShader = false;
        }

        protected override void Draw(ISkiaSharpApiLease lease, Vector size)
        {
            if (_state is not { Opacity: > 0.001, Color.A: > 0 } state) return;
            var r = GetResources(ShaderEffect.Value);
            var width = (float)size.X;
            var height = (float)size.Y;
            var radius = Math.Clamp((float)state.Radius, 0, Math.Min(width, height) * 0.5f);

            if (!_hasCachedShader || _cachedSize != size)
            {
                r.Set2("uSize", width, height);
                r.Uniforms["uRadius"] = radius;
                r.Uniforms["uTime"] = (float)_time;
                r.Uniforms["uOpacity"] = (float)Math.Clamp(state.Opacity, 0, 1);
                r.SetColor("uColor", state.Color);
                using var shader = ShaderEffect.Value.ToShader(r.Uniforms);
                // The paint retains the native shader, including its frozen phase when the rate reaches zero.
                r.Paint.Shader = shader;
                _hasCachedShader = true;
                _cachedSize = size;
            }

            // Skia supplies device-scale-aware rounded edge coverage; the shader colors the interior.
            r.Paint.IsAntialias = true;
            r.Paint.Color = SKColors.White.WithAlpha((byte)Math.Round(lease.CurrentOpacity * byte.MaxValue));
            lease.SkCanvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, r.Paint);
        }
    }

    // Glow depth, intensity, highlight width, and animation speed deliberately remain shader constants.
    private const string ShaderSource =
        """
        uniform float2 uSize;
        uniform float uRadius;
        uniform float uTime;
        uniform float uOpacity;
        layout(color) uniform float4 uColor;

        const float SPEED = 0.17;
        const float INTENSITY = 0.90;
        const float GLOW_DEPTH = 0.16;
        const float HIGHLIGHT_WIDTH = 0.065;
        const float TAU = 6.28318530718;

        float sdRoundBox(float2 p, float2 halfSize, float radius) {
            float2 q = abs(p) - halfSize + radius;
            return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - radius;
        }

        float cyclicDistance(float a, float b) {
            float d = abs(a - b);
            return min(d, 1.0 - d);
        }

        float pulse(float u, float head, float width) {
            float x = cyclicDistance(u, head) / max(width, 0.0001);
            return exp(-x * x);
        }

        float4 main(float2 position) {
            float2 size = max(uSize, float2(1.0));
            float2 halfSize = size * 0.5;
            float radius = clamp(uRadius, 0.0, min(halfSize.x, halfSize.y));
            float2 p = position - halfSize;
            float distance = sdRoundBox(p, halfSize, radius);

            // The antialiased SKRoundRect supplies outer-edge coverage. Values sampled just beyond
            // the mathematical contour still need edge color so Skia can blend partial pixels;
            // therefore the shader must not discard them with a binary distance test.
            float inward = max(-distance, 0.0);
            float2 normalized = p / max(halfSize, float2(1.0));
            float u = fract(atan(normalized.y, normalized.x) / TAU + 0.5 + 0.11);
            float head = fract(uTime * SPEED);
            float primary = pulse(u, head, HIGHLIGHT_WIDTH);
            float secondary = 0.56 * pulse(u, fract(head + 0.5), HIGHLIGHT_WIDTH * 1.8);
            float depth = max(min(size.x, size.y) * GLOW_DEPTH, 1.0);
            float core = exp(-pow(inward / max(depth * 0.22, 0.001), 1.65));
            float dye = exp(-pow(inward / depth, 1.30));
            float energy = primary + secondary;
            float alpha = clamp((core * 0.48 + dye * 0.26) * energy * INTENSITY * uOpacity, 0.0, 0.82) * uColor.a;
            float3 hot = mix(uColor.rgb, float3(1.0), primary * 0.24);
            return float4(hot * alpha, alpha);
        }
        """;
}
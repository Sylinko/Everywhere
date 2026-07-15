using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
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

    public static readonly StyledProperty<Color> GlowColorProperty = GlowBorder.GlowColorProperty.AddOwner<GlowBorderOverlay>();
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = TemplatedControl.CornerRadiusProperty.AddOwner<GlowBorderOverlay>();
    public static readonly StyledProperty<double> GlowOpacityProperty = GlowBorder.GlowOpacityProperty.AddOwner<GlowBorderOverlay>();

    private static readonly Lazy<SKRuntimeEffect> ShaderEffect = new(CreateEffect);

    private TopLevel? _topLevel;
    private bool _animationStarted;
    private TimeSpan _time;

    static GlowBorderOverlay()
    {
        AffectsRender<GlowBorderOverlay>(GlowColorProperty, CornerRadiusProperty, GlowOpacityProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == GlowOpacityProperty || change.Property == IsVisibleProperty) StartAnimation();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(this);
        StartAnimation();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _topLevel = null;
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || GlowOpacity <= 0.001 || GlowColor.A == 0) return;
        context.Custom(new GlowDrawOperation(new Rect(Bounds.Size), GlowColor, CornerRadius.TopLeft, GlowOpacity, _time.TotalSeconds));
    }

    private void StartAnimation()
    {
        if (_animationStarted || _topLevel is null || !IsVisible || GlowOpacity <= 0.001) return;

        _animationStarted = true;
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        if (_topLevel is null || !IsVisible || GlowOpacity <= 0.001)
        {
            _animationStarted = false;
            return;
        }

        _time = time;
        InvalidateVisual();
        _topLevel.RequestAnimationFrame(OnAnimationFrame);
    }

    private static SKRuntimeEffect CreateEffect() =>
        SKRuntimeEffect.CreateShader(ShaderSource, out var errorText) ??
        throw new InvalidOperationException("Failed to compile GlowBorder shader: " + errorText);

    private sealed class GlowDrawOperation(Rect bounds, Color color, double radius, double opacity, double time) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var effect = ShaderEffect.Value;
            var width = (float)Bounds.Width;
            var height = (float)Bounds.Height;
            var clampedRadius = Math.Clamp((float)radius, 0, Math.Min(width, height) * 0.5f);
            using var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["uSize"] = new[] { width, height };
            uniforms["uRadius"] = clampedRadius;
            uniforms["uTime"] = (float)time;
            uniforms["uOpacity"] = (float)Math.Clamp(opacity, 0, 1);
            uniforms["uColor"] = new[] { color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f };
            using var children = new SKRuntimeEffectChildren(effect);
            using var shader = effect.ToShader(uniforms, children);
            using var paint = new SKPaint();
            paint.IsAntialias = true;
            paint.Shader = shader;
            paint.BlendMode = SKBlendMode.SrcOver;

            // Skia owns the rounded silhouette and its device-scale-aware pixel coverage. The
            // runtime shader deliberately remains responsible only for distance-based interior
            // color, avoiding the hard one-sample SDF cutoff that previously aliased at corners.
            lease.SkCanvas.DrawRoundRect(new SKRect(0, 0, width, height), clampedRadius, clampedRadius, paint);
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }
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
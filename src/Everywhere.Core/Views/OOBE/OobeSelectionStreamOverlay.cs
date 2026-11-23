using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.VisualTree;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// Renders a wide, softly diffused tricolor Skia funnel halo extending from behind the
/// companion orb down to the selected choice card, staying strictly beneath the card surfaces.
/// </summary>
public sealed class OobeSelectionStreamOverlay : Control
{
    /// <summary>
    /// Defines the <see cref="SourceVisual"/> property.
    /// </summary>
    public static readonly StyledProperty<Visual?> SourceVisualProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, Visual?>(nameof(SourceVisual));

    /// <summary>
    /// Defines the <see cref="TargetsRoot"/> property.
    /// </summary>
    public static readonly StyledProperty<Visual?> TargetsRootProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, Visual?>(nameof(TargetsRoot));

    /// <summary>
    /// Defines the <see cref="TargetKey"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TargetKeyProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, string?>(nameof(TargetKey));

    /// <summary>
    /// Defines the <see cref="IsHoldFadeIn"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsHoldFadeInProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, bool>(nameof(IsHoldFadeIn));

    /// <summary>
    /// Defines the <see cref="PrimaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> PrimaryColorProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, Color>(nameof(PrimaryColor), Color.FromRgb(77, 184, 255));

    /// <summary>
    /// Defines the <see cref="SecondaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, Color>(nameof(SecondaryColor), Color.FromRgb(240, 86, 184));

    /// <summary>
    /// Defines the <see cref="TertiaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> TertiaryColorProperty =
        AvaloniaProperty.Register<OobeSelectionStreamOverlay, Color>(nameof(TertiaryColor), Color.FromRgb(220, 230, 92));

    /// <summary>
    /// Gets or sets the origin visual (typically the companion orb) where the funnel halo starts.
    /// </summary>
    public Visual? SourceVisual
    {
        get => GetValue(SourceVisualProperty);
        set => SetValue(SourceVisualProperty, value);
    }

    /// <summary>
    /// Gets or sets the container subtree searched for the active named target card.
    /// </summary>
    public Visual? TargetsRoot
    {
        get => GetValue(TargetsRootProperty);
        set => SetValue(TargetsRootProperty, value);
    }

    /// <summary>
    /// Gets or sets the control <see cref="Control.Name"/> of the selected target card, or <see langword="null"/> to fade out.
    /// </summary>
    public string? TargetKey
    {
        get => GetValue(TargetKeyProperty);
        set => SetValue(TargetKeyProperty, value);
    }

    /// <summary>
    /// Gets or sets whether entering fade-in waits until step-transition sliding finishes,
    /// keeping the beam anchored in place instead of flying across from left/right.
    /// </summary>
    public bool IsHoldFadeIn
    {
        get => GetValue(IsHoldFadeInProperty);
        set => SetValue(IsHoldFadeInProperty, value);
    }

    /// <summary>
    /// Gets or sets the first tricolor brand stop shared with the companion halo.
    /// </summary>
    public Color PrimaryColor
    {
        get => GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    /// <summary>
    /// Gets or sets the second tricolor brand stop shared with the companion halo.
    /// </summary>
    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    /// <summary>
    /// Gets or sets the third tricolor brand stop shared with the companion halo.
    /// </summary>
    public Color TertiaryColor
    {
        get => GetValue(TertiaryColorProperty);
        set => SetValue(TertiaryColorProperty, value);
    }

    private const string FunnelShader = """
                                        uniform float2 uSize;
                                        uniform float2 uPixelScale;
                                        uniform float2 uOrigin;
                                        uniform float2 uTargetCenter;
                                        uniform float uTargetHalfWidth;
                                        uniform float4 uCardRectA;
                                        uniform float4 uCardRectB;
                                        uniform float uTime;
                                        uniform float uOpacity;
                                        layout(color) uniform float4 uColorA;
                                        layout(color) uniform float4 uColorB;
                                        layout(color) uniform float4 uColorC;

                                        float hash12(float2 p) {
                                            float3 p3 = fract(float3(p.xyx) * 0.1031);
                                            p3 += dot(p3, p3.yzx + 33.33);
                                            return fract((p3.x + p3.y) * p3.z);
                                        }

                                        float triangularNoise(float2 p) {
                                            return hash12(p) - hash12(p + 19.19);
                                        }

                                        float lobe(float2 uv, float angle, float eccentricity, float focus) {
                                            float2 dir = float2(cos(angle), sin(angle)) * eccentricity;
                                            float d = length(uv - dir);
                                            return exp(-focus * d * d);
                                        }

                                        float sdRoundedBox(float2 p, float4 rect, float r) {
                                            float2 center = (rect.xy + rect.zw) * 0.5;
                                            float2 halfSize = (rect.zw - rect.xy) * 0.5;
                                            if (halfSize.x <= 1.0 || halfSize.y <= 1.0) {
                                                return 1000.0;
                                            }
                                            float2 q = abs(p - center) - halfSize + r;
                                            return length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - r;
                                        }

                                        float4 main(float2 p) {
                                            float dyTotal = max(uTargetCenter.y - uOrigin.y, 40.0);
                                            float t = (p.y - uOrigin.y) / dyTotal;
                                            if (t < -0.22 || t > 1.32) {
                                                return float4(0.0);
                                            }

                                            float cardSd = min(sdRoundedBox(p, uCardRectA, 16.0), sdRoundedBox(p, uCardRectB, 16.0));
                                            float cardMask = smoothstep(-1.5, 2.5, cardSd);
                                            if (cardMask <= 0.001) {
                                                return float4(0.0);
                                            }

                                            float clampedT = clamp(t, 0.0, 1.0);
                                            float smoothT = clampedT * clampedT * (3.0 - 2.0 * clampedT);
                                            float centerlineT = mix(clampedT, smoothT, 0.35);
                                            float beamX = mix(uOrigin.x, uTargetCenter.x, centerlineT);

                                            float neckHalfW = 56.0;
                                            float baseHalfW = max(uTargetHalfWidth * 0.90, 128.0);
                                            float flare = mix(clampedT, smoothT, 0.35);
                                            float halfW = neckHalfW + (baseHalfW - neckHalfW) * flare;

                                            float xRatio = abs(p.x - beamX) / max(halfW, 1.0);
                                            if (xRatio >= 1.12) {
                                                return float4(0.0);
                                            }

                                            float lateral = exp(-0.75 * xRatio * xRatio) * smoothstep(1.12, 0.20, xRatio);
                                            float topFade = smoothstep(-0.24, 0.16, t);
                                            float bottomFade = 1.0 - smoothstep(0.90, 1.16, t);

                                            float verticalEnergy = mix(0.86, 1.0, clampedT);
                                            float wave = 0.90 + 0.10 * sin(t * 4.5 - uTime * 1.8);
                                            float rimAura = exp(-20.0 * (t - 0.98) * (t - 0.98)) * exp(-1.10 * xRatio * xRatio) * smoothstep(1.08, 0.18, xRatio);

                                            float alpha = (lateral * topFade * bottomFade * verticalEnergy * wave * 0.16 + rimAura * 0.06) * cardMask * uOpacity;
                                            if (alpha <= 0.001) {
                                                return float4(0.0);
                                            }

                                            float2 localUV = float2((p.x - beamX) / max(halfW, 1.0), clampedT * 1.15 - 0.55);
                                            float angle = uTime * 0.42;
                                            float wA = lobe(localUV, angle, 0.40, 2.2);
                                            float wB = lobe(localUV, angle + 2.0943951, 0.40, 2.2);
                                            float wC = lobe(localUV, angle + 4.1887902, 0.40, 2.2);
                                            float sumW = max(wA + wB + wC, 0.001);
                                            float3 tricolor = (uColorA.rgb * wA + uColorB.rgb * wB + uColorC.rgb * wC) / sumW;

                                            float edgeMask = 1.0 - smoothstep(0.90, 1.12, xRatio);
                                            float noise = triangularNoise(p * uPixelScale);
                                            alpha = clamp(alpha + noise * (2.4 / 255.0) * edgeMask * cardMask, 0.0, 1.0);

                                            float3 premul = tricolor * alpha + noise * (0.85 / 255.0) * edgeMask * cardMask;
                                            premul = clamp(premul, float3(0.0), float3(alpha));
                                            return float4(premul, alpha);
                                        }
                                        """;

    private static readonly Lazy<SKRuntimeEffect> ShaderEffect = new(() =>
        SKRuntimeEffect.CreateShader(FunnelShader, out var error) ??
        throw new InvalidOperationException("Failed to compile OOBE selection funnel shader: " + error));

    private readonly OobeShaderClock _clock;
    private bool _hasGeometry;
    private Point _lastStart;
    private Point _currentEnd;
    private float _currentTargetHalfWidth;
    private Rect _cardRectA;
    private Rect _cardRectB;
    private float _opacity;
    private float _targetOpacity;

    static OobeSelectionStreamOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<OobeSelectionStreamOverlay>(false);
        AffectsRender<OobeSelectionStreamOverlay>(
            SourceVisualProperty,
            TargetsRootProperty,
            TargetKeyProperty,
            IsHoldFadeInProperty,
            PrimaryColorProperty,
            SecondaryColorProperty,
            TertiaryColorProperty);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OobeSelectionStreamOverlay"/> class.
    /// </summary>
    public OobeSelectionStreamOverlay()
    {
        _clock = new OobeShaderClock(this, StepAnimation, () => _targetOpacity > 0.002f || _opacity > 0.002f);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        UpdateTargetState();
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
        if (change.Property == TargetKeyProperty ||
            change.Property == IsHoldFadeInProperty ||
            change.Property == SourceVisualProperty ||
            change.Property == TargetsRootProperty)
        {
            UpdateTargetState();
            _clock.Synchronize();
        }
        else if (change.Property == IsVisibleProperty || change.Property == OpacityProperty)
        {
            _clock.Synchronize();
        }
    }

    /// <inheritdoc />
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_hasGeometry || _opacity <= 0.002f || Bounds.Width <= 1 || Bounds.Height <= 1) return;

        context.Custom(
            new FunnelDrawOperation(
                new Rect(Bounds.Size),
                new SKPoint((float)_lastStart.X, (float)_lastStart.Y),
                new SKPoint((float)_currentEnd.X, (float)_currentEnd.Y),
                _currentTargetHalfWidth,
                _cardRectA,
                _cardRectB,
                PrimaryColor,
                SecondaryColor,
                TertiaryColor,
                _opacity,
                _clock.ElapsedSeconds));
    }

    private void UpdateTargetState()
    {
        _targetOpacity = !IsHoldFadeIn && !string.IsNullOrEmpty(TargetKey) ? 1f : 0f;

        if (_targetOpacity > 0f && TryResolveGeometry(out var start, out var end, out var halfWidth, out var cardA, out var cardB))
        {
            var isFreshFadeIn = !_hasGeometry || _opacity <= 0.004f;
            _lastStart = start;
            _cardRectA = cardA;
            _cardRectB = cardB;
            _hasGeometry = true;
            if (isFreshFadeIn)
            {
                _currentEnd = end;
                _currentTargetHalfWidth = halfWidth;
            }
        }

        InvalidateVisual();
    }

    private void StepAnimation(double dt)
    {
        if (_targetOpacity > 0f && TryResolveGeometry(out var start, out var end, out var halfWidth, out var cardA, out var cardB))
        {
            var isFreshFadeIn = !_hasGeometry || _opacity <= 0.004f;
            _lastStart = start;
            _cardRectA = cardA;
            _cardRectB = cardB;
            _hasGeometry = true;
            if (isFreshFadeIn)
            {
                _currentEnd = end;
                _currentTargetHalfWidth = halfWidth;
            }
            else if (dt > 0)
            {
                var posRate = 1f - MathF.Exp((float)(-dt * 12.0));
                _currentEnd = new Point(
                    _currentEnd.X + (end.X - _currentEnd.X) * posRate,
                    _currentEnd.Y + (end.Y - _currentEnd.Y) * posRate);
                _currentTargetHalfWidth += (halfWidth - _currentTargetHalfWidth) * posRate;
            }
        }

        if (dt > 0)
        {
            var opacitySpeed = _targetOpacity > _opacity ? 8.5 : 15.0;
            var opacityStep = 1f - MathF.Exp((float)(-dt * opacitySpeed));
            _opacity += (_targetOpacity - _opacity) * opacityStep;
            if (MathF.Abs(_opacity - _targetOpacity) < 0.004f) _opacity = _targetOpacity;
        }

        if (_targetOpacity <= 0f && _opacity <= 0.002f)
        {
            _opacity = 0f;
            _hasGeometry = false;
            _clock.Synchronize();
        }
    }

    private bool TryResolveGeometry(out Point start, out Point end, out float targetHalfWidth, out Rect cardA, out Rect cardB)
    {
        start = default;
        end = default;
        targetHalfWidth = 0f;
        cardA = default;
        cardB = default;

        if (SourceVisual is not { IsEffectivelyVisible: true } source ||
            source.Bounds.Width <= 1 ||
            source.Bounds.Height <= 1 ||
            TargetsRoot is not { IsEffectivelyVisible: true } root ||
            string.IsNullOrEmpty(TargetKey))
        {
            return false;
        }

        if (source.TranslatePoint(new Point(source.Bounds.Width * 0.5, source.Bounds.Height * 0.56), this) is not { } resolvedStart)
        {
            return false;
        }

        Control? target = null;
        foreach (var child in root.GetVisualDescendants())
        {
            if (child is not Control { IsEffectivelyVisible: true, Bounds: { Width: > 1, Height: > 1 } } control) continue;

            if (string.Equals(control.Name, "SourceCloudCard", StringComparison.Ordinal))
            {
                cardA = ResolveRectInOverlay(control);
            }
            else if (string.Equals(control.Name, "SourceOwnCard", StringComparison.Ordinal))
            {
                cardB = ResolveRectInOverlay(control);
            }

            if (string.Equals(control.Name, TargetKey, StringComparison.Ordinal))
            {
                target = control;
            }
        }

        if (target is null ||
            target.TranslatePoint(new Point(target.Bounds.Width * 0.5, 0.0), this) is not { } resolvedEnd)
        {
            return false;
        }

        start = resolvedStart;
        end = resolvedEnd;
        targetHalfWidth = (float)(target.Bounds.Width * 0.5);
        return true;
    }

    private Rect ResolveRectInOverlay(Control control) =>
        control.TranslatePoint(default, this) is { } origin ? new Rect(origin, control.Bounds.Size) : default;

    private sealed class FunnelDrawOperation(
        Rect bounds,
        SKPoint origin,
        SKPoint targetCenter,
        float targetHalfWidth,
        Rect cardRectA,
        Rect cardRectB,
        Color colorA,
        Color colorB,
        Color colorC,
        float funnelOpacity,
        double time
    ) : ICustomDrawOperation
    {
        public Rect Bounds { get; } = bounds;

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;

            var width = (float)Bounds.Width;
            var height = (float)Bounds.Height;
            var opacity = Math.Clamp((float)lease.CurrentOpacity * funnelOpacity, 0f, 1f);
            if (width <= 1f || height <= 1f || opacity <= 0.002f) return;

            var save = canvas.Save();
            try
            {
                canvas.Translate((float)Bounds.X, (float)Bounds.Y);
                canvas.ClipRect(new SKRect(0, 0, width, height));

                var matrix = canvas.TotalMatrix;
                var scaleX = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
                var scaleY = MathF.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);

                var effect = ShaderEffect.Value;
                using var uniforms = new SKRuntimeEffectUniforms(effect);
                uniforms["uSize"] = new[] { width, height };
                uniforms["uPixelScale"] = new[] { scaleX, scaleY };
                uniforms["uOrigin"] = new[] { origin.X, origin.Y };
                uniforms["uTargetCenter"] = new[] { targetCenter.X, targetCenter.Y };
                uniforms["uTargetHalfWidth"] = targetHalfWidth;
                uniforms["uCardRectA"] = ToFloat4(cardRectA);
                uniforms["uCardRectB"] = ToFloat4(cardRectB);
                uniforms["uTime"] = (float)time;
                uniforms["uOpacity"] = opacity;
                uniforms["uColorA"] = OobeShaderClock.ColorComponents(colorA);
                uniforms["uColorB"] = OobeShaderClock.ColorComponents(colorB);
                uniforms["uColorC"] = OobeShaderClock.ColorComponents(colorC);

                using var children = new SKRuntimeEffectChildren(effect);
                using var shader = effect.ToShader(uniforms, children);
                using var paint = new SKPaint();
                paint.Shader = shader;
                paint.BlendMode = SKBlendMode.SrcOver;
                paint.IsAntialias = false;
                paint.IsDither = true;
                canvas.DrawRect(0, 0, width, height, paint);
            }
            finally
            {
                canvas.RestoreToCount(save);
            }
        }

        private static float[] ToFloat4(Rect rect) =>
            [(float)rect.X, (float)rect.Y, (float)rect.Right, (float)rect.Bottom];

        public bool HitTest(Point p) => false;
        public void Dispose() { }
        public bool Equals(ICustomDrawOperation? other) => false;
    }
}

using Avalonia.Controls;
using Avalonia.Media;
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

    private const string FunnelShader =
        """
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

    private readonly ShaderVisualHost _host;
    private State? _lastState;

    static OobeSelectionStreamOverlay()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<OobeSelectionStreamOverlay>(false);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OobeSelectionStreamOverlay"/> class.
    /// </summary>
    public OobeSelectionStreamOverlay()
    {
        _host = new ShaderVisualHost(this, static () => new Handler(), UpdateGeometry);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LayoutUpdated += HandleLayoutUpdated;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LayoutUpdated -= HandleLayoutUpdated;
        _lastState = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void HandleLayoutUpdated(object? sender, EventArgs e) => UpdateGeometry();

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetKeyProperty || change.Property == IsHoldFadeInProperty ||
            change.Property == SourceVisualProperty || change.Property == TargetsRootProperty ||
            change.Property == PrimaryColorProperty || change.Property == SecondaryColorProperty || change.Property == TertiaryColorProperty)
        {
            UpdateGeometry();
        }
    }

    /// <summary>
    /// Snapshots card geometry after layout or a transform-only movement of the companion or cards.
    /// </summary>
    internal void UpdateGeometry()
    {
        if (!_host.IsAttached) return;

        var state = new State(ResolveTarget(), PrimaryColor, SecondaryColor, TertiaryColor);
        if (_lastState == state) return;

        _lastState = state;
        _host.Send(state);
    }

    private TargetGeometry? ResolveTarget()
    {
        if (IsHoldFadeIn || string.IsNullOrEmpty(TargetKey) ||
            SourceVisual is not { IsEffectivelyVisible: true, Bounds: { Width: > 1, Height: > 1 } } source ||
            TargetsRoot is not { IsEffectivelyVisible: true } root ||
            source.TranslatePoint(new Point(source.Bounds.Width * 0.5, source.Bounds.Height * 0.56), this) is not { } origin)
        {
            return null;
        }

        var target = default(Control);
        var cardA = default(Rect);
        var cardB = default(Rect);
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

            if (string.Equals(control.Name, TargetKey, StringComparison.Ordinal)) target = control;
        }

        if (target?.TranslatePoint(new Point(target.Bounds.Width * 0.5, 0.0), this) is not { } end) return null;
        return new TargetGeometry(origin, end, (float)(target.Bounds.Width * 0.5), cardA, cardB);
    }

    private Rect ResolveRectInOverlay(Control control)
    {
        return control.TranslatePoint(default, this) is { } origin ? new Rect(origin, control.Bounds.Size) : default;
    }

    private readonly record struct TargetGeometry(Point Origin, Point End, float HalfWidth, Rect CardA, Rect CardB);

    private readonly record struct State(TargetGeometry? Target, Color Primary, Color Secondary, Color Tertiary);

    private sealed class Handler : SkiaShaderVisualHandler
    {
        protected override bool IsAnimating => _state.Target.HasValue || _opacity > 0.002f;

        private State _state;
        private TargetGeometry _lastTarget;
        private Point _currentEnd;
        private float _currentHalfWidth;
        private float _opacity;
        private double _elapsedSeconds;

        protected override void OnStateChanged(object message)
        {
            _state = (State)message;
            // Fade out at the last valid target instead of snapping to the origin when selection clears.
            if (_state.Target is not { } target) return;

            _lastTarget = target;
            if (_opacity <= 0.004f)
            {
                _currentEnd = target.End;
                _currentHalfWidth = target.HalfWidth;
            }
        }

        protected override void Advance(double dt)
        {
            _elapsedSeconds += dt;
            if (_state.Target is { } target)
            {
                var positionStep = 1f - MathF.Exp((float)(-dt * 12.0));
                _currentEnd += (target.End - _currentEnd) * positionStep;
                _currentHalfWidth += (target.HalfWidth - _currentHalfWidth) * positionStep;
            }

            var targetOpacity = _state.Target.HasValue ? 1f : 0f;
            var opacitySpeed = targetOpacity > _opacity ? 8.5 : 15.0;
            _opacity += (targetOpacity - _opacity) * (1f - MathF.Exp((float)(-dt * opacitySpeed)));
            if (MathF.Abs(_opacity - targetOpacity) < 0.004f) _opacity = targetOpacity;
        }

        protected override void Draw(ISkiaSharpApiLease lease, Vector size)
        {
            var opacity = Math.Clamp((float)lease.CurrentOpacity * _opacity, 0f, 1f);
            if (opacity <= 0.002f || size.X <= 1 || size.Y <= 1) return;

            var resources = GetResources(ShaderEffect.Value);
            resources.Set2("uSize", (float)size.X, (float)size.Y);
            resources.SetPixelScale("uPixelScale", lease.SkCanvas);
            resources.Set2("uOrigin", (float)_lastTarget.Origin.X, (float)_lastTarget.Origin.Y);
            resources.Set2("uTargetCenter", (float)_currentEnd.X, (float)_currentEnd.Y);
            resources.Uniforms["uTargetHalfWidth"] = _currentHalfWidth;
            var cardA = _lastTarget.CardA;
            var cardB = _lastTarget.CardB;
            resources.Set4("uCardRectA", (float)cardA.X, (float)cardA.Y, (float)cardA.Right, (float)cardA.Bottom);
            resources.Set4("uCardRectB", (float)cardB.X, (float)cardB.Y, (float)cardB.Right, (float)cardB.Bottom);
            resources.Uniforms["uTime"] = (float)_elapsedSeconds;
            resources.Uniforms["uOpacity"] = opacity;
            resources.SetColor("uColorA", _state.Primary);
            resources.SetColor("uColorB", _state.Secondary);
            resources.SetColor("uColorC", _state.Tertiary);
            resources.DrawRect(lease.SkCanvas, size);
        }
    }
}

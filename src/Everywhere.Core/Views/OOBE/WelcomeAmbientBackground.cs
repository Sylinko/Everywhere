using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Skia;
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

    private const string Shader =
        """
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

    private readonly ShaderVisualHost _host;

    static WelcomeAmbientBackground()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<WelcomeAmbientBackground>(false);
    }

    /// <summary>
    /// Creates the animated backdrop for the onboarding scenes.
    /// </summary>
    public WelcomeAmbientBackground()
    {
        _host = new ShaderVisualHost(this, static () => new Handler(), SendState);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BaseColorProperty || change.Property == CoolColorProperty ||
            change.Property == AccentColorProperty || change.Property == WarmColorProperty)
        {
            SendState();
        }
    }

    private void SendState()
    {
        if (_host.IsAttached) _host.Send(new Palette(BaseColor, CoolColor, AccentColor, WarmColor));
    }

    private sealed record Palette(Color Base, Color Cool, Color Accent, Color Warm);

    private sealed class Handler : SkiaShaderVisualHandler
    {
        protected override bool IsAnimating => _palette is not null;

        private Palette? _palette;
        private double _elapsedSeconds;

        protected override void OnStateChanged(object message) => _palette = (Palette)message;

        protected override void Advance(double dt) => _elapsedSeconds += dt;

        protected override void Draw(ISkiaSharpApiLease lease, Vector size)
        {
            if (_palette is not { } palette) return;

            var resources = GetResources(ShaderEffect.Value);
            resources.Set2("size", (float)size.X, (float)size.Y);
            resources.SetColor("baseColor", palette.Base);
            resources.SetColor("coolColor", palette.Cool);
            resources.SetColor("accentColor", palette.Accent);
            resources.SetColor("warmColor", palette.Warm);
            resources.Uniforms["opacity"] = (float)lease.CurrentOpacity;
            resources.Uniforms["uTime"] = (float)_elapsedSeconds;
            resources.DrawRect(lease.SkCanvas, size);
        }
    }
}

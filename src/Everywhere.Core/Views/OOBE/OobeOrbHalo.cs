using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Skia;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// Renders a subtle animated tricolor Skia shader halo behind the onboarding companion orb,
/// blending Everywhere's three brand hues and transitioning smoothly to an emerald palette on completion.
/// </summary>
public sealed class OobeOrbHalo : Control
{
    /// <summary>
    /// Defines the <see cref="PrimaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> PrimaryColorProperty =
        AvaloniaProperty.Register<OobeOrbHalo, Color>(nameof(PrimaryColor), Color.FromRgb(77, 184, 255));

    /// <summary>
    /// Defines the <see cref="SecondaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> SecondaryColorProperty =
        AvaloniaProperty.Register<OobeOrbHalo, Color>(nameof(SecondaryColor), Color.FromRgb(240, 86, 184));

    /// <summary>
    /// Defines the <see cref="TertiaryColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> TertiaryColorProperty =
        AvaloniaProperty.Register<OobeOrbHalo, Color>(nameof(TertiaryColor), Color.FromRgb(220, 230, 92));

    /// <summary>
    /// Defines the <see cref="SuccessColor"/> property.
    /// </summary>
    public static readonly StyledProperty<Color> SuccessColorProperty =
        AvaloniaProperty.Register<OobeOrbHalo, Color>(nameof(SuccessColor), Color.FromRgb(16, 185, 129));

    /// <summary>
    /// Defines the <see cref="IsSuccess"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsSuccessProperty =
        AvaloniaProperty.Register<OobeOrbHalo, bool>(nameof(IsSuccess));

    public Color PrimaryColor
    {
        get => GetValue(PrimaryColorProperty);
        set => SetValue(PrimaryColorProperty, value);
    }

    public Color SecondaryColor
    {
        get => GetValue(SecondaryColorProperty);
        set => SetValue(SecondaryColorProperty, value);
    }

    public Color TertiaryColor
    {
        get => GetValue(TertiaryColorProperty);
        set => SetValue(TertiaryColorProperty, value);
    }

    public Color SuccessColor
    {
        get => GetValue(SuccessColorProperty);
        set => SetValue(SuccessColorProperty, value);
    }

    public bool IsSuccess
    {
        get => GetValue(IsSuccessProperty);
        set => SetValue(IsSuccessProperty, value);
    }

    private const string Shader =
        """
        uniform float2 uSize;
        uniform float2 uPixelScale;
        uniform float uTime;
        uniform float uOpacity;
        uniform float uSuccessMix;
        layout(color) uniform float4 uColorA;
        layout(color) uniform float4 uColorB;
        layout(color) uniform float4 uColorC;
        layout(color) uniform float4 uSuccessColor;

        float smootherstep01(float x) {
            x = clamp(x, 0.0, 1.0);
            return x * x * x * (x * (x * 6.0 - 15.0) + 10.0);
        }

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

        float4 main(float2 p) {
            float2 size = max(uSize, float2(1.0));
            float2 center = size * 0.5;
            float radius = min(size.x, size.y) * 0.5;
            float2 uv = (p - center) / max(radius, 1.0);
            float t = length(uv);
            if (t >= 1.0) {
                return float4(0.0);
            }

            float angle = uTime * 0.42;
            float breathe = 0.90 + 0.10 * sin(uTime * 1.15);

            float wA = lobe(uv, angle, 0.24, 4.6);
            float wB = lobe(uv, angle + 2.0943951, 0.24, 4.6);
            float wC = lobe(uv, angle + 4.1887902, 0.24, 4.6);
            float sumW = max(wA + wB + wC, 0.001);
            float3 brandMix = (uColorA.rgb * wA + uColorB.rgb * wB + uColorC.rgb * wC) / sumW;

            float3 successMixColor = mix(uSuccessColor.rgb, uColorA.rgb, 0.28 * (0.5 + 0.5 * sin(uv.x * 3.0 - uTime * 0.9)));
            float3 haloColor = mix(brandMix, successMixColor, uSuccessMix);

            float coreLift = exp(-7.2 * dot(uv, uv));
            haloColor = mix(haloColor, float3(1.0), coreLift * 0.22);

            float fade = 1.0 - smootherstep01(t);
            float energy = 0.72 + 0.28 * clamp(sumW * 0.65, 0.0, 1.0);
            float alpha = pow(fade, 1.45) * 0.42 * breathe * energy * uOpacity;

            float edgeMask = 1.0 - smoothstep(0.92, 1.0, t);
            float noise = triangularNoise(p * uPixelScale);
            alpha = clamp(alpha + noise * (3.5 / 255.0) * edgeMask, 0.0, 1.0);

            float3 premul = haloColor * alpha + noise * (1.25 / 255.0) * edgeMask;
            premul = clamp(premul, float3(0.0), float3(alpha));
            return float4(premul, alpha);
        }
        """;

    private static readonly Lazy<SKRuntimeEffect> ShaderEffect = new(() =>
        SKRuntimeEffect.CreateShader(Shader, out var error) ??
        throw new InvalidOperationException("Failed to compile OOBE orb halo shader: " + error));

    private readonly ShaderVisualHost _host;

    static OobeOrbHalo()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<OobeOrbHalo>(false);
    }

    /// <summary>
    /// Creates the animated companion halo.
    /// </summary>
    public OobeOrbHalo()
    {
        _host = new ShaderVisualHost(this, static () => new Handler(), SendState);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PrimaryColorProperty || change.Property == SecondaryColorProperty ||
            change.Property == TertiaryColorProperty || change.Property == SuccessColorProperty || change.Property == IsSuccessProperty)
        {
            SendState();
        }
    }

    private void SendState()
    {
        if (_host.IsAttached)
        {
            _host.Send(new State(PrimaryColor, SecondaryColor, TertiaryColor, SuccessColor, IsSuccess));
        }
    }

    private sealed record State(Color Primary, Color Secondary, Color Tertiary, Color Success, bool IsSuccess);

    private sealed class Handler : SkiaShaderVisualHandler
    {
        protected override bool IsAnimating => _state is not null;

        private State? _state;
        private double _elapsedSeconds;
        private double _successMix;

        protected override void OnStateChanged(object message)
        {
            var state = (State)message;
            if (_state is null) _successMix = state.IsSuccess ? 1 : 0;
            _state = state;
        }

        protected override void Advance(double dt)
        {
            _elapsedSeconds += dt;
            var target = _state is { IsSuccess: true } ? 1.0 : 0.0;
            _successMix = Math.Abs(_successMix - target) <= 0.001 ? target : _successMix + (target - _successMix) * Math.Min(1.0, dt * 3.0);
        }

        protected override void Draw(ISkiaSharpApiLease lease, Vector size)
        {
            if (_state is not { } state) return;

            var resources = GetResources(ShaderEffect.Value);
            resources.Set2("uSize", (float)size.X, (float)size.Y);
            resources.SetPixelScale("uPixelScale", lease.SkCanvas);
            resources.Uniforms["uTime"] = (float)_elapsedSeconds;
            resources.Uniforms["uOpacity"] = (float)lease.CurrentOpacity;
            resources.Uniforms["uSuccessMix"] = (float)_successMix;
            resources.SetColor("uColorA", state.Primary);
            resources.SetColor("uColorB", state.Secondary);
            resources.SetColor("uColorC", state.Tertiary);
            resources.SetColor("uSuccessColor", state.Success);
            resources.DrawRect(lease.SkCanvas, size);
        }
    }
}

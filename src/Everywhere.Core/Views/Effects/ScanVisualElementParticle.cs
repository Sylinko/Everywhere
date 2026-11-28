using Avalonia.Controls;
using Avalonia.Skia;
using Avalonia.Threading;
using Everywhere.Utilities;
using Serilog;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// Draws a 1.6-second scan over a captured window mask.
/// </summary>
public class ScanVisualElementParticle : UserControl, IVisualElementParticle
{
    /// <inheritdoc />
    public bool IsSelfAnimating => true;

    private const double DurationMs = 1600d;
    // Allow compositor startup latency and >100 ms delta clamping to settle before recycling via UI fallback.
    private const double FallbackTimeoutMs = DurationMs + 800d;
    private readonly ShaderVisualHost _host;

    private VisualEffectImage<SKImage>? _windowMaskRef;
    private double _elapsedMs;
    private long _generation;
    private long _fallbackTimerGeneration;
    private DispatcherTimer? _fallbackTimer;
    private Action? _onCompleted;

    /// <summary>
    /// Creates a particle that can be recycled across successive screen captures.
    /// </summary>
    public ScanVisualElementParticle()
    {
        _host = new ShaderVisualHost(this, () => new Handler(OnCompositorCompleted), SendState);
    }

    /// <inheritdoc />
    public void RegisterCompletionCallback(Action? callback) => _onCompleted = callback;

    /// <inheritdoc />
    public void Spawn(
        VisualElementEffectWindow owner,
        Point startPosition,
        IParticleTargetTracker? targetTracker,
        object? startContent,
        object? endContent,
        Size startSize)
    {
        var generation = ++_generation;
        DisposeHelper.DisposeToDefault(ref _windowMaskRef);
        _windowMaskRef = startContent.NotNull<VisualEffectImage<SKImage>>();
        _windowMaskRef.AddRef();

        Width = startSize.Width;
        Height = startSize.Height;
        Canvas.SetLeft(this, startPosition.X - startSize.Width / 2d);
        Canvas.SetTop(this, startPosition.Y - startSize.Height / 2d);

        _elapsedMs = 0;
        RestartFallbackTimer(generation);
        SendState();
    }

    /// <inheritdoc />
    public void Recycle()
    {
        _generation++;
        _fallbackTimer?.Stop();
        DisposeHelper.DisposeToDefault(ref _windowMaskRef);
        SendState();
    }

    /// <inheritdoc />
    public bool Update(double deltaTimeMs)
    {
        if (_windowMaskRef is null) return true;
        if (deltaTimeMs > 0) _elapsedMs += deltaTimeMs;
        return _elapsedMs >= DurationMs;
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _fallbackTimer?.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void SendState()
    {
        if (!_host.IsAttached) return;

        // The pool can attach before or after Spawn. Transfer a separate reference with each
        // start message: a recycle on the UI thread must not invalidate a queued render.
        _windowMaskRef?.AddRef();
        _host.Send(new ScanState(_windowMaskRef, _elapsedMs, _generation));
    }

    private void RestartFallbackTimer(long generation)
    {
        _fallbackTimerGeneration = generation;
        if (_fallbackTimer is null)
        {
            _fallbackTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(FallbackTimeoutMs)
            };
            _fallbackTimer.Tick += HandleFallbackTimerTick;
        }
        else
        {
            _fallbackTimer.Stop();
        }

        _fallbackTimer.Start();
    }

    private void HandleFallbackTimerTick(object? sender, EventArgs e)
    {
        _fallbackTimer?.Stop();
        CompleteIfCurrent(_fallbackTimerGeneration);
    }

    private void OnCompositorCompleted(long generation) => CompleteIfCurrent(generation);

    private void CompleteIfCurrent(long generation)
    {
        if (generation != _generation || _windowMaskRef is null) return;

        _fallbackTimer?.Stop();
        _elapsedMs = DurationMs;
        _onCompleted?.Invoke();
    }

    private sealed record ScanState(VisualEffectImage<SKImage>? Mask, double ElapsedMs, long Generation);

    /// <summary>
    /// Uses the captured screenshot's alpha to follow the target window's shape and rounded corners.
    /// </summary>
    private sealed class Handler(Action<long> onCompleted) : SkiaShaderVisualHandler
    {
        private static readonly Lazy<SKRuntimeEffect?> FluidEffect = new(CreateEffect);

        protected override bool IsAnimating => _windowMaskRef is not null && _elapsedMs < DurationMs;

        private VisualEffectImage<SKImage>? _windowMaskRef;
        private SKShader? _maskShader;
        private SKPaint? _maskPaint;
        private SKPaint? _layerPaint;
        private Vector _maskSize;
        private double _elapsedMs;
        private long _generation;
        private bool _hasNotifiedCompletion;

        private static SKRuntimeEffect? CreateEffect()
        {
            const string sksl =
                """
                uniform float2 u_resolution;
                uniform float u_time;
                uniform float u_progress; // Controls the drop (0.0 to 1.0)
                uniform shader u_mask;    // The captured window screenshot for alpha masking
                
                const float SCAN_SPEED = 2.2;
                const float EVAPORATION_SPEED = 0.6;
                const float GLOW = 6.0;
                const float OPACITY = 1.0;
                const float EDGE_WIDTH = 8.0;
                
                // 2D rotation matrix
                float2x2 rot(float a) {
                    float s = sin(a), c = cos(a);
                    return float2x2(c, -s, s, c);
                }
                
                half eval(float2 fragCoord) {
                    return u_mask.eval(fragCoord).a;
                }
                
                half4 main(float2 fragCoord) {
                    // Normalize coordinates and aspect ratio
                    float2 uv = fragCoord.xy / u_resolution.xy;
                    float2 st = uv;
                    st.x *= u_resolution.x / u_resolution.y;
                
                    // Fluid Dynamics with turbulence
                    float2 p = st * 3.0;
                    float t = u_time * SCAN_SPEED;
                    for(int i = 0; i < 3; i++) {
                        p = p * rot(1.5) + float2(sin(p.y + t), cos(p.x - t)) * 0.3;
                    }
                
                    // Colors
                    float3 color0 = float3(0.322, 0.690, 0.969);
                    float3 color1 = float3(0.929, 0.310, 0.710);
                    float3 color2 = float3(0.937, 0.886, 0.494);
                    float mix1 = sin(p.x * 0.8) * 0.5 + 0.5;
                    float mix2 = cos(p.y * 0.6) * 0.5 + 0.5;
                    float3 fluidColor = mix(color0, color1, mix1);
                    fluidColor = mix(fluidColor, color2, mix2);
                    fluidColor *= 1.1;
                
                    // --- 45-Degree Diagonal Scan Logic ---
                    // Project UVs onto a diagonal axis. 
                    float scanAxis = (2.0 - 0.8 * uv.x -  1.2 * uv.y) * 0.5 + 0.5;
                    
                    float scanEdge = 1.8 - (u_progress * 2.4);
                    // Add noise to the diagonal line
                    float edgeNoise = (sin(p.x * 2.0) + cos(p.y * 1.5)) * 0.012;
                    
                    // Calculate distance based on the diagonal axis instead of just uv.y
                    float dist = scanAxis - scanEdge + edgeNoise;
                    
                    float frontEdge = smoothstep(-0.15, 0.05, dist);
                    float evaporateTail = smoothstep(1.0, 0.0, dist * EVAPORATION_SPEED);
                    
                    float scanVisibility = frontEdge * evaporateTail;
                    float dynamicThicknessVariation = (sin(p.y * 2.0 + t) * cos(p.x * 3.0 - t)) * 0.15;
                    float glowStartBoundary = -0.15 + dynamicThicknessVariation;
                    float glowEndBoundary = 0.40 + dynamicThicknessVariation;
                
                    // Glow on the leading edge of the scan
                    float leadingEdgeGlow = smoothstep(glowStartBoundary, 0.0, dist) * smoothstep(glowEndBoundary, 0.0, dist);
                    fluidColor += leadingEdgeGlow * float3(1.0, 0.9, 0.9) * GLOW;
                
                    // --- Laplacian Edge Detect ---
                    // Using an 8-neighbor Laplacian kernel for sharp edge detection:
                    //  1   1   1
                    //  1  -8   1
                    //  1   1   1
                    float fluidNoise = (sin(p.y * 1.5 + t) * cos(p.x * 2.5 - t)) * 0.5 + 0.5;
                    float w = EDGE_WIDTH * (0.2 + fluidNoise * 1.4);
                    
                    // Sample the 8 surrounding neighbors
                    float a00 = eval(fragCoord + float2(-w, -w));
                    float a10 = eval(fragCoord + float2(0.0, -w));
                    float a20 = eval(fragCoord + float2(w, -w));
                    float a01 = eval(fragCoord + float2(-w, 0.0));
                    float a21 = eval(fragCoord + float2(w, 0.0));
                    float a02 = eval(fragCoord + float2(-w, w));
                    float a12 = eval(fragCoord + float2(0.0, w));
                    float a22 = eval(fragCoord + float2(w, w));
                    
                    // Sample the center pixel
                    float centerAlpha = u_mask.eval(fragCoord).a;
                
                    // Calculate the Laplacian sum
                    float laplacian = a00 + a10 + a20 + a01 + a21 + a02 + a12 + a22 - 8.0 * centerAlpha;
                    
                    // Take the absolute value because the Laplacian transitions from positive to negative across an edge
                    float edgeIntensity = clamp(abs(laplacian) * (1.5 + fluidNoise * 1.5), 0.0, 1.0);
                
                    float finalAlpha = scanVisibility * edgeIntensity * smoothstep(0.5, 1.0, centerAlpha) * OPACITY;
                    return half4(fluidColor * finalAlpha, finalAlpha); // premultiplied color
                }
                """;

            var effect = SKRuntimeEffect.CreateShader(sksl, out var errors);
            if (effect is null) Log.ForContext<Handler>().Error("Failed to compile SkSL shader: {Errors}", errors);
            return effect;
        }

        protected override void OnStateChanged(object message)
        {
            var state = (ScanState)message;
            ReleaseResources();
            _windowMaskRef = state.Mask;
            _elapsedMs = state.ElapsedMs;
            _generation = state.Generation;
            _hasNotifiedCompletion = false;
        }

        protected override void Advance(double dt)
        {
            _elapsedMs = Math.Min(_elapsedMs + dt * 1000d, DurationMs);
            if (_elapsedMs >= DurationMs && !_hasNotifiedCompletion)
            {
                _hasNotifiedCompletion = true;
                var generation = _generation;
                Dispatcher.UIThread.Post(() => onCompleted(generation));
            }
        }

        protected override void Draw(ISkiaSharpApiLease lease, Vector size)
        {
            if (_elapsedMs >= DurationMs ||
                size is not { X: > 16 and < 5120, Y: > 16 and < 5120 } ||
                _windowMaskRef?.Image is not { } mask || FluidEffect.Value is not { } effect)
            {
                return;
            }

            var resources = GetResources(effect);
            if (_maskPaint is null)
            {
                _maskPaint = new SKPaint { IsAntialias = true };

                // SKPaint retains the native filter until ReleaseResources disposes the paint.
                using var blur = SKImageFilter.CreateBlur(5f, 5f);
                resources.Paint.ImageFilter = blur;
                resources.Paint.BlendMode = SKBlendMode.SrcIn;
                resources.Paint.IsAntialias = true;
            }

            if (_maskSize != size)
            {
                _maskShader?.Dispose();
                var scale = SKMatrix.CreateScale((float)(size.X / mask.Width), (float)(size.Y / mask.Height));
                _maskShader = mask.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, scale);
                _maskPaint.Shader = _maskShader;
                resources.Children["u_mask"] = _maskShader;
                resources.Set2("u_resolution", (float)size.X, (float)size.Y);
                _maskSize = size;
            }

            resources.Uniforms["u_time"] = (float)(_elapsedMs / 1000d);
            resources.Uniforms["u_progress"] = (float)(_elapsedMs / DurationMs) * 1.2f;
            using var shader = effect.ToShader(resources.Uniforms, resources.Children);
            resources.Paint.Shader = shader;

            var rect = new SKRect(0, 0, (float)size.X, (float)size.Y);
            var canvas = lease.SkCanvas;
            var layerPaint = _layerPaint ??= new SKPaint();
            layerPaint.ColorF = new SKColorF(1f, 1f, 1f, (float)lease.CurrentOpacity);
            var saveCount = canvas.SaveLayer(rect, layerPaint);
            try
            {
                canvas.DrawRect(rect, _maskPaint);
                canvas.DrawRect(rect, resources.Paint);
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        protected override void ReleaseResources()
        {
            base.ReleaseResources();
            DisposeHelper.DisposeToDefault(ref _maskPaint);
            DisposeHelper.DisposeToDefault(ref _layerPaint);
            DisposeHelper.DisposeToDefault(ref _maskShader);
            DisposeHelper.DisposeToDefault(ref _windowMaskRef);
            _maskSize = default;
        }
    }
}
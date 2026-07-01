using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Everywhere.Utilities;
using Serilog;
using SkiaSharp;

namespace Everywhere.Views;

public class ScanVisualElementParticle : VisualElementParticle
{
    private RefCountedSKImage? _windowMaskRef;
    private double _animationProgress;

    public override void Spawn(Point startPosition, IParticleTargetTracker? targetTracker, object? startContent, object? endContent, Size startSize)
    {
        _windowMaskRef = new RefCountedSKImage(startContent.NotNull<SKImage>());

        Width = startSize.Width;
        Height = startSize.Height;
        Canvas.SetLeft(this, startPosition.X - startSize.Width / 2d);
        Canvas.SetTop(this, startPosition.Y - startSize.Height / 2d);

        _animationProgress = 0d;
    }

    public override void Recycle()
    {
        DisposeHelper.DisposeToDefault(ref _windowMaskRef);
    }

    public override bool Update(double deltaTimeMs)
    {
        if (_windowMaskRef is null) return true;
        if (deltaTimeMs <= 0) return false;

        _animationProgress += deltaTimeMs / 1600d;
        InvalidateVisual();

        return _animationProgress >= 1d;
    }

    public override void Render(DrawingContext context)
    {
        if (_windowMaskRef is null) return;
        if (Bounds is not { Width: > 16d and < 5120, Height: > 16d and < 5120 }) return;

        context.Custom(new FluidScanDrawOperation(
            this,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d));
    }

    private sealed class RefCountedSKImage(SKImage image) : IDisposable
    {
        private int _refCount = 1;

        public SKImage? Image { get; private set; } = image;

        public void AddRef()
        {
            if (Image != null)
            {
                Interlocked.Increment(ref _refCount);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Decrement(ref _refCount) != 0) return;

            Image?.Dispose();
            Image = null;
        }
    }

    /// <summary>
    /// A custom drawing operation that renders a fluid scanline effect over a target window bounds.
    /// It uses a captured screenshot of the window as an alpha mask to perfectly match the window's shape/rounded corners.
    /// </summary>
    private sealed class FluidScanDrawOperation : ICustomDrawOperation
    {
        private static readonly SKRuntimeEffect? FluidEffect;

        static FluidScanDrawOperation()
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

            FluidEffect = SKRuntimeEffect.CreateShader(sksl, out var errors);
            if (FluidEffect == null)
            {
                Log.ForContext<FluidScanDrawOperation>().Error("Failed to compile SkSL shader: {Errors}", errors);
            }
        }

        private readonly float _timeSeconds;
        private readonly Rect _bounds;
        private readonly RefCountedSKImage? _windowMaskRef;
        private readonly float _progress;

        /// <summary>
        /// Creates a new frame of the fluid scan operation.
        /// </summary>
        /// <param name="owner"></param>
        /// <param name="timeSeconds">The continuously increasing time in seconds (for fluid swirling).</param>
        public FluidScanDrawOperation(ScanVisualElementParticle owner, double timeSeconds)
        {
            _timeSeconds = (float)timeSeconds;
            _bounds = new Rect(0d, 0d, owner.Width, owner.Height);
            _windowMaskRef = owner._windowMaskRef;
            _progress = (float)owner._animationProgress * 1.2f;
            _windowMaskRef?.AddRef();
        }

        public Rect Bounds => _bounds;

        public void Render(ImmediateDrawingContext context)
        {
            if (FluidEffect is null) return;
            if (_windowMaskRef is not { Image: { } windowMask }) return;

            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            var canvas = lease.SkCanvas;

            var saveCount = canvas.Save();
            canvas.Translate((float)_bounds.X, (float)_bounds.Y);

            var drawRect = new SKRect(0, 0, (float)_bounds.Width, (float)_bounds.Height);

            using var saveLayerPaint = new SKPaint();
            canvas.SaveLayer(drawRect, saveLayerPaint);

            var scaleX = (float)(_bounds.Width / windowMask.Width);
            var scaleY = (float)(_bounds.Height / windowMask.Height);
            var localMatrix = SKMatrix.CreateScale(scaleX, scaleY);
            using var maskShader = windowMask.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, localMatrix);

            using var maskPaint = new SKPaint();
            maskPaint.Shader = maskShader;
            maskPaint.IsAntialias = true;
            canvas.DrawRect(drawRect, maskPaint);

            using var uniforms = new SKRuntimeEffectUniforms(FluidEffect);
            uniforms.Add("u_resolution", new[] { (float)_bounds.Width, (float)_bounds.Height });
            uniforms.Add("u_time", _timeSeconds);
            uniforms.Add("u_progress", _progress);

            using var children = new SKRuntimeEffectChildren(FluidEffect);
            children.Add("u_mask", maskShader);

            using (var fluidShader = FluidEffect.ToShader(uniforms, children))
            using (var blurFilter = SKImageFilter.CreateBlur(5f, 5f))
            using (var fluidPaint = new SKPaint())
            {
                fluidPaint.Shader = fluidShader;
                fluidPaint.ImageFilter = blurFilter;
                fluidPaint.BlendMode = SKBlendMode.SrcIn;
                fluidPaint.IsAntialias = true;
                canvas.DrawRect(drawRect, fluidPaint);
            }

            canvas.RestoreToCount(saveCount);
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose() => _windowMaskRef?.Dispose();
    }
}
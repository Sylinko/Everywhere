using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using SkiaSharp;

namespace Everywhere.Views;

/// <summary>
/// Runs an effect on the compositor clock. The compositor serializes messages, ticks and draws,
/// so mutable shader inputs and native resources stay on one thread.
/// </summary>
internal abstract class SkiaShaderVisualHandler : CompositionCustomVisualHandler
{
    protected abstract bool IsAnimating { get; }

    private bool _isShown;
    private TimeSpan? _lastTick;
    private ShaderResources? _resources;

    public sealed override void OnMessage(object message)
    {
        switch (message)
        {
            case ShaderVisualMessage.Dispose:
                _isShown = false;
                ReleaseResources();
                return;
            case ShaderVisualMessage.Show:
                _isShown = true;
                break;
            case ShaderVisualMessage.Hide:
                _isShown = false;
                break;
            default:
                OnStateChanged(message);
                break;
        }

        Invalidate();
        if (_isShown && IsAnimating) RegisterForNextAnimationFrameUpdate();
        else _lastTick = null;
    }

    public sealed override void OnAnimationFrameUpdate()
    {
        if (!_isShown || !IsAnimating) return;

        var now = CompositionNow;
        var dt = Math.Clamp((now - (_lastTick ?? now)).TotalSeconds, 0, 0.1);
        _lastTick = now;
        Advance(dt);
        Invalidate();
        if (IsAnimating) RegisterForNextAnimationFrameUpdate();
        else _lastTick = null;
    }

    public sealed override void OnRender(ImmediateDrawingContext context)
    {
        var size = EffectiveSize;
        // Do not call RenderClipIntersectes(GetRenderBounds()): in Avalonia 12.2.0,
        // CompositionCustomVisualHandler stores _currentTransformedClip in root/window
        // coordinates while _currentTransform remains Identity, so local GetRenderBounds()
        // gets falsely rejected whenever the visual is positioned away from (0, 0).
        if (!_isShown || size.X <= 0 || size.Y <= 0) return;
        if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;

        using var lease = feature.Lease();
        if (lease.CurrentOpacity > 0.001) Draw(lease, size);
    }

    protected abstract void OnStateChanged(object message);

    protected abstract void Advance(double dt);

    protected abstract void Draw(ISkiaSharpApiLease lease, Vector size);

    protected ShaderResources GetResources(SKRuntimeEffect effect) => _resources ??= new ShaderResources(effect);

    protected virtual void ReleaseResources()
    {
        _resources?.Dispose();
        _resources = null;
    }
}

/// <summary>
/// Retains an effect's paint and uniform storage across draws. SkiaSharp copies vector values
/// into uniform storage when set, so all uniforms of the same width can share a scratch array.
/// </summary>
internal sealed class ShaderResources(SKRuntimeEffect effect) : IDisposable
{
    public SKPaint Paint { get; } = new();

    public SKRuntimeEffectUniforms Uniforms { get; } = new(effect);

    public SKRuntimeEffectChildren Children => _children ??= new SKRuntimeEffectChildren(effect);

    private readonly float[] _pair = new float[2];
    private readonly float[] _quad = new float[4];

    private SKRuntimeEffectChildren? _children;

    public void Set2(string name, float x, float y)
    {
        _pair[0] = x;
        _pair[1] = y;
        Uniforms[name] = _pair;
    }

    public void Set4(string name, float x, float y, float z, float w)
    {
        _quad[0] = x;
        _quad[1] = y;
        _quad[2] = z;
        _quad[3] = w;
        Uniforms[name] = _quad;
    }

    public void SetColor(string name, Color color)
    {
        Set4(name, color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
    }

    public void SetPixelScale(string name, SKCanvas canvas)
    {
        var matrix = canvas.TotalMatrix;
        var x = MathF.Sqrt(matrix.ScaleX * matrix.ScaleX + matrix.SkewY * matrix.SkewY);
        var y = MathF.Sqrt(matrix.SkewX * matrix.SkewX + matrix.ScaleY * matrix.ScaleY);
        Set2(name, x, y);
    }

    public void DrawRect(SKCanvas canvas, Vector size)
    {
        using var shader = effect.ToShader(Uniforms);
        Paint.Shader = shader;
        Paint.IsDither = true;
        canvas.DrawRect(0, 0, (float)size.X, (float)size.Y, Paint);
    }

    public void Dispose()
    {
        Paint.Dispose();
        _children?.Dispose();
        Uniforms.Dispose();
    }
}

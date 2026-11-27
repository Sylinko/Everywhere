// Copyright (c) Wiesław Šoltés. Licensed under MIT; see LICENSE.txt.
// Patched from Svg.Controls.Skia.Avalonia 12.2.0 to respect ISkiaSharpApiLease.CurrentOpacity.
using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using MonoMod;
using SkiaSharp;
using Svg.Skia;

namespace Everywhere.Patches.Svg.Controls.Skia.Avalonia;

/// <summary>
/// Applies Avalonia's accumulated <see cref="ISkiaSharpApiLease.CurrentOpacity"/> when rendering
/// <see cref="global::Avalonia.Svg.Skia.SvgCustomDrawOperation"/> so SVG visuals participate in
/// opacity and page-transition fades instead of rendering at constant opacity 1.
/// </summary>
[MonoModPatch("Avalonia.Svg.Skia.SvgCustomDrawOperation")]
internal class patch_SvgCustomDrawOperation : ICustomDrawOperation
{
    [MonoModIgnore]
    private readonly SKSvg? _svg;

    [MonoModIgnore]
    public extern Rect Bounds { get; }

    [MonoModIgnore]
    public extern void Dispose();

    [MonoModIgnore]
    public extern bool HitTest(Point p);

    [MonoModIgnore]
    public extern bool Equals(ICustomDrawOperation? other);

    [MonoModReplace]
    public void Render(ImmediateDrawingContext context)
    {
        if (_svg is null)
        {
            return;
        }

        var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (leaseFeature is null)
        {
            return;
        }

        using var lease = leaseFeature.Lease();
        if (lease.SkCanvas is not { } canvas)
        {
            return;
        }

        // Everywhere: honor Avalonia's accumulated opacity (e.g. CrossFade and Visual.Opacity).
        var opacity = lease.CurrentOpacity;
        if (opacity <= 0d)
        {
            return;
        }

        if (opacity >= 1d - 1e-4)
        {
            _svg.Draw(canvas);
            return;
        }

        using var paint = new SKPaint();
        paint.ColorF = new SKColorF(1f, 1f, 1f, (float)opacity);
        var saveCount = canvas.SaveLayer(paint);
        try
        {
            _svg.Draw(canvas);
        }
        finally
        {
            canvas.RestoreToCount(saveCount);
        }
    }
}

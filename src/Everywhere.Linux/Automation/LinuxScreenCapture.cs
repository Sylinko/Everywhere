using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Extensions;
using Everywhere.Linux.Desktop;
using SkiaSharp;

namespace Everywhere.Linux.Automation;

/// <summary>
/// Owns a bounded visible-desktop capture. Bounds and pixel density are independent.
/// </summary>
public sealed class LinuxScreenCapture : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelRect Bounds { get; }

    /// <inheritdoc />
    public PixelFormat Format => PixelFormat.Bgra8888;

    /// <inheritdoc />
    public AlphaFormat AlphaFormat => AlphaFormat.Premul;

    /// <inheritdoc />
    public nint Data => _bitmap?.GetPixels() ?? 0;

    /// <inheritdoc />
    public PixelSize Size { get; }

    /// <inheritdoc />
    public int Stride { get; }

    private SKBitmap? _bitmap;

    private LinuxScreenCapture(SKBitmap bitmap, PixelRect bounds)
    {
        _bitmap = bitmap;
        Bounds = bounds;
        Size = new PixelSize(bitmap.Width, bitmap.Height);
        Stride = bitmap.RowBytes;
    }

    /// <summary>
    /// Captures and composes intersecting outputs without knowing their screen-copy protocol.
    /// </summary>
    public static async Task<IVisualElementCapture> CaptureAsync(
        ICompositorStateProvider compositor,
        ISilentScreenCapturer capturer,
        PixelRect bounds,
        CancellationToken cancellationToken)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new NotSupportedException("The capture region is empty.");
        }

        var outputs = (await compositor.GetOutputsAsync(cancellationToken).ConfigureAwait(false)).Where(output => output.Bounds.Intersects(bounds))
            .ToArray();
        if (outputs.Length == 0)
        {
            throw new NotSupportedException("The capture region does not intersect an active output.");
        }

        var density = outputs.Max(OutputDensity);
        var size = IVisualElementCapture.LimitOutputSize(
            new PixelSize(
                Math.Max(1, (int)Math.Ceiling(bounds.Width * density)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * density))));
        var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            foreach (var output in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var capture = await capturer.CaptureOutputAsync(output.Name, cancellationToken).ConfigureAwait(false);
                if (capture.Bounds != output.Bounds)
                {
                    throw new InvalidOperationException("The output geometry changed during capture.");
                }

                using var source = capture.ToSKImage() ?? throw new InvalidDataException("The output capture has no pixels.");
                var region = bounds.Intersect(output.Bounds);
                var sourceDensity = OutputDensity(output);
                var sourceRect = new SKRect(
                    (float)((region.X - output.Bounds.X) * sourceDensity),
                    (float)((region.Y - output.Bounds.Y) * sourceDensity),
                    (float)((region.Right - output.Bounds.X) * sourceDensity),
                    (float)((region.Bottom - output.Bounds.Y) * sourceDensity));
                var destination = new SKRect(
                    (float)((region.X - bounds.X) * (double)size.Width / bounds.Width),
                    (float)((region.Y - bounds.Y) * (double)size.Height / bounds.Height),
                    (float)((region.Right - bounds.X) * (double)size.Width / bounds.Width),
                    (float)((region.Bottom - bounds.Y) * (double)size.Height / bounds.Height));
                canvas.DrawImage(source, sourceRect, destination, new SKSamplingOptions(SKFilterMode.Linear), null);
            }

            return new LinuxScreenCapture(bitmap, bounds);
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Preserves native density without multiplying the rounded logical output dimensions.
    /// </summary>
    public static double OutputDensity(OutputInfo output)
    {
        return Math.Min(output.Scale, (double)IVisualElementCapture.MaximumDimension / Math.Max(output.Bounds.Width, output.Bounds.Height));
    }

    /// <summary>
    /// Normalizes a bounded backend image to the transport's BGRA premultiplied pixel format.
    /// </summary>
    public static IVisualElementCapture Decode(byte[] png, PixelRect bounds)
    {
        using var decoded = SKBitmap.Decode(png) ?? throw new InvalidDataException("Invalid screenshot image.");
        var size = IVisualElementCapture.LimitOutputSize(new PixelSize(decoded.Width, decoded.Height));
        var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawBitmap(decoded, new SKRect(0, 0, size.Width, size.Height), new SKSamplingOptions(SKFilterMode.Linear), null);
        return new LinuxScreenCapture(bitmap, bounds);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Utilities;
using SkiaSharp;

namespace Everywhere.Windows.Interop;

/// <summary>Owns a bounded copy of a Windows BGRA capture while preserving its screen-space coverage.</summary>
public sealed class ResizedScreenCapture : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelRect Bounds { get; }

    /// <inheritdoc />
    public PixelSize Size { get; }

    /// <inheritdoc />
    public PixelFormat Format => PixelFormat.Bgra8888;

    /// <inheritdoc />
    public AlphaFormat AlphaFormat { get; }

    /// <inheritdoc />
    public nint Data => _bitmap?.GetPixels() ?? 0;

    /// <inheritdoc />
    public int Stride { get; }

    private SKBitmap? _bitmap;

    private ResizedScreenCapture(IVisualElementCapture source, PixelSize size)
    {
        Bounds = source.Bounds;
        Size = size;
        AlphaFormat = source.AlphaFormat;
        var alpha = source.AlphaFormat == AlphaFormat.Opaque ? SKAlphaType.Opaque : SKAlphaType.Premul;
        var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, alpha));
        try
        {
            using var pixels = new SKPixmap(
                new SKImageInfo(source.Size.Width, source.Size.Height, SKColorType.Bgra8888, alpha),
                source.Data,
                source.Stride);
            using var destination = bitmap.PeekPixels();

            if (!pixels.ScalePixels(destination, new SKSamplingOptions(SKCubicResampler.CatmullRom)))
                throw new InvalidOperationException("Failed to resize captured pixels.");

            Stride = bitmap.RowBytes;
            _bitmap = bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Consumes an owned BGRA capture. Returns it unchanged when already bounded; otherwise disposes it after copying to bounded storage, including on failure.</summary>
    public static IVisualElementCapture Limit(IVisualElementCapture source)
    {
        try
        {
            var size = IVisualElementCapture.LimitOutputSize(source.Size);
            if (size == source.Size) return source;
            using (source) return new ResizedScreenCapture(source, size);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeHelper.DisposeToDefault(ref _bitmap);
}
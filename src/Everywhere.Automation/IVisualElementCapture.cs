using Avalonia;
using Avalonia.Platform;

namespace Everywhere.Automation;

/// <summary>
/// Exposes an owned bitmap buffer captured from a visual element.
/// </summary>
/// <remarks>
/// Size, Stride, and Data describe bitmap pixels. Bounds uses the platform desktop coordinates expected by Avalonia (points on macOS, pixels on Windows/X11), with a top-left origin convention. No image DPI is implied; presentation adapters choose their own density metadata.
/// </remarks>
public interface IVisualElementCapture : IDisposable
{
    /// <summary>Gets the represented region in platform desktop coordinates, independently of the bitmap's pixel resolution.</summary>
    PixelRect Bounds { get; }

    /// <summary>Gets the captured pixel format.</summary>
    PixelFormat Format { get; }

    /// <summary>Gets the captured alpha format.</summary>
    AlphaFormat AlphaFormat { get; }

    /// <summary>Gets the address of the captured pixel buffer.</summary>
    nint Data { get; }

    /// <summary>Gets the actual pixel dimensions of the captured buffer.</summary>
    PixelSize Size { get; }

    /// <summary>Gets the number of bytes between adjacent bitmap rows.</summary>
    int Stride { get; }

    /// <summary>Gets the maximum output length of either image dimension.</summary>
    const int MaximumDimension = 4096;

    /// <summary>Fits a nonempty pixel size within the output limit without upscaling or changing its aspect ratio beyond integer rounding.</summary>
    static PixelSize LimitOutputSize(PixelSize source)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(source.Height);

        if (source is { Width: <= MaximumDimension, Height: <= MaximumDimension }) return source;

        var scale = Math.Min(1d, (double)MaximumDimension / Math.Max(source.Width, source.Height));
        return new PixelSize(Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
    }
}
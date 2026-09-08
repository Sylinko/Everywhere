using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Utilities;
using SkiaSharp;
using X11;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>Owns bounded copied pixels independently of the temporary XImage.</summary>
public sealed class X11CapturedBitmapData : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelRect Bounds { get; }

    /// <inheritdoc />
    public PixelFormat Format => PixelFormat.Bgra8888;

    /// <inheritdoc />
    public AlphaFormat AlphaFormat { get; }

    /// <inheritdoc />
    public nint Data => _bitmap?.GetPixels() ?? 0;

    /// <inheritdoc />
    public PixelSize Size { get; }

    /// <inheritdoc />
    public int Stride { get; }

    private SKBitmap? _bitmap;

    /// <summary>Copies or scales a borrowed XImage; its caller destroys the source on both success and failure.</summary>
    public X11CapturedBitmapData(XImage xImage, PixelRect bounds)
    {
        Bounds = bounds;
        Size = IVisualElementCapture.LimitOutputSize(new PixelSize(xImage.width, xImage.height));
        if (xImage.byte_order != 0) throw new NotSupportedException("Big-endian XImage capture is not supported.");

        var format = DeterminePixelFormat(xImage);
        var colorType = format == PixelFormat.Bgra8888 ?
            SKColorType.Bgra8888 :
            format == PixelFormat.Rgba8888 ? SKColorType.Rgba8888 : SKColorType.Rgb565;
        AlphaFormat = xImage.depth == 32 ? AlphaFormat.Unpremul : AlphaFormat.Opaque;
        var alpha = xImage.depth == 32 ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        var bitmap = new SKBitmap(new SKImageInfo(Size.Width, Size.Height, SKColorType.Bgra8888, alpha));
        try
        {
            using var source = new SKPixmap(new SKImageInfo(xImage.width, xImage.height, colorType, alpha), xImage.data, xImage.bytes_per_line);
            using var destination = bitmap.PeekPixels();

            if (!source.ScalePixels(destination, new SKSamplingOptions(SKCubicResampler.CatmullRom)))
                throw new InvalidOperationException("Failed to copy X11 pixels.");

            Stride = bitmap.RowBytes;
            _bitmap = bitmap;
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Infers the pixel format from XImage masks and bits_per_pixel.
    /// </summary>
    private static PixelFormat DeterminePixelFormat(XImage img)
    {
        switch (img.bits_per_pixel)
        {
            // Note: Mask values are represented as integers in the machine's native endianness.
            // On standard little-endian x86/ARM machines:
            // Bgra8888 means Byte 0=B, Byte 1=G, Byte 2=R, Byte 3=A.
            // When read as a 32-bit uint, R is at 0x00FF0000.
            case 32 when img is { red_mask: 0x00FF0000, green_mask: 0x0000FF00, blue_mask: 0x000000FF }:
                return PixelFormat.Bgra8888;
            case 32 when img is { red_mask: 0x000000FF, green_mask: 0x0000FF00, blue_mask: 0x00FF0000 }:
                return PixelFormat.Rgba8888;
            case 16 when img is { red_mask: 0xF800, green_mask: 0x07E0, blue_mask: 0x001F }:
                return PixelFormat.Rgb565;
            default:
                throw new NotSupportedException(
                    $"Unsupported XImage format: bpp={img.bits_per_pixel}, R={img.red_mask:X}, G={img.green_mask:X}, B={img.blue_mask:X}");
        }
    }

    /// <inheritdoc />
    public void Dispose() => DisposeHelper.DisposeToDefault(ref _bitmap);
}
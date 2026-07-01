using Avalonia;
using Avalonia.Platform;
using Everywhere.Interop;
using X11;

namespace Everywhere.Linux.Interop.X11Backend;

using System;

public class X11CapturedBitmapData(XImage xImage) : IVisualElement.ICapturedBitmapData
{
    public PixelFormat Format { get; } = DeterminePixelFormat(xImage);
    public AlphaFormat AlphaFormat { get; } = xImage.depth == 32 ? AlphaFormat.Unpremul : AlphaFormat.Opaque;
    public nint Data => _xImage.data;
    public PixelSize Size { get; } = new(xImage.width, xImage.height);
    public Vector Dpi { get; } = new(96, 96);
    public int Stride => _xImage.bytes_per_line;

    private XImage _xImage = xImage;
    private bool _disposed;

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

    ~X11CapturedBitmapData() => Dispose();

    void IDisposable.Dispose()
    {
        Dispose();
        GC.SuppressFinalize(this);
    }

    private void Dispose()
    {
        if (_disposed) return;

        if (_xImage.data != IntPtr.Zero)
        {
            Xutil.XDestroyImage(ref _xImage);
            _xImage = default;
        }
        _disposed = true;
    }
}
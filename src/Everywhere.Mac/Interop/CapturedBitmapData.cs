using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;

namespace Everywhere.Mac.Interop;

/// <summary>Owns a bounded RGBA bitmap and its independently specified desktop coverage.</summary>
public sealed class CapturedBitmapData : SafeHandle, IVisualElementCapture
{
    /// <inheritdoc />
    public PixelRect Bounds { get; }
    /// <inheritdoc />
    public PixelFormat Format { get; }
    /// <inheritdoc />
    public AlphaFormat AlphaFormat { get; }
    /// <inheritdoc />
    public nint Data => handle;
    /// <inheritdoc />
    public PixelSize Size { get; }
    /// <inheritdoc />
    public int Stride { get; }

    /// <summary>Creates an empty owned capture for an element without a drawable region.</summary>
    public static CapturedBitmapData Empty => new();

    private CapturedBitmapData() : base(0, true)
    {
        Format = PixelFormat.Rgba8888;
        AlphaFormat = AlphaFormat.Premul;
        Size = new PixelSize(0, 0);
        Stride = 0;
    }

    /// <summary>Draws a borrowed CGImage into bounded owned storage without changing its desktop coverage.</summary>
    public CapturedBitmapData(CGImage cgImage, PixelRect bounds) : base(0, true)
    {
        Format = PixelFormat.Rgba8888;
        AlphaFormat = AlphaFormat.Premul;

        Bounds = bounds;
        Size = VisualElementCapture.GetOutputSize(new PixelSize(checked((int)cgImage.Width), checked((int)cgImage.Height)));
        var width = Size.Width;
        var height = Size.Height;

        Stride = checked(width * 4);

        SetHandle(Marshal.AllocHGlobal(checked(Stride * height)));

        try
        {
            using var colorSpace = CGColorSpace.CreateDeviceRGB();
            const int bitsPerComponent = 8;
            using var context = new CGBitmapContext(Data, width, height, bitsPerComponent, Stride, colorSpace, CGImageAlphaInfo.PremultipliedLast);

            // Allocate the destination at its final resolution, not an intermediate full-size RGBA copy.
            // TODO(macOS): Verify row orientation and channel order with an asymmetric colored test image.
            var destination = new CGRect(0, 0, width, height);
            context.ClearRect(destination);
            context.DrawImage(destination, cgImage);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(Data);
        return true;
    }

    public override bool IsInvalid => handle == 0;
}
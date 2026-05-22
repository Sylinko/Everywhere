using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;

namespace Everywhere.Mac.Interop;

public sealed class CapturedBitmapData : SafeHandle, ILockedFramebuffer
{
    public nint Address => handle;
    public PixelSize Size { get; }
    public int RowBytes { get; }
    public Vector Dpi { get; }
    public PixelFormat Format { get; }

    public static CapturedBitmapData Empty => new();

    private CapturedBitmapData() : base(0, true)
    {
        Format = PixelFormat.Rgba8888;
        Size = new PixelSize(0, 0);
        Dpi = new Vector(0, 0);
        RowBytes = 0;
    }

    public CapturedBitmapData(CGImage cgImage, double scaleFactor) : base(0, true)
    {
        Format = PixelFormat.Rgba8888;

        var width = (int)cgImage.Width;
        var height = (int)cgImage.Height;

        Size = new PixelSize(width, height);
        Dpi = new Vector(72 * scaleFactor, 72 * scaleFactor);
        RowBytes = width * 4;

        SetHandle(Marshal.AllocHGlobal(RowBytes * height));

        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        const int bitsPerComponent = 8;
        using var context = new CGBitmapContext(
            Address,
            width,
            height,
            bitsPerComponent,
            RowBytes,
            colorSpace,
            CGImageAlphaInfo.PremultipliedLast);

        context.DrawImage(new CGRect(0, 0, width, height), cgImage);
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(Address);
        return true;
    }

    public override bool IsInvalid => handle == 0;
}
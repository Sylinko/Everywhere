using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Interop;

namespace Everywhere.Mac.Interop;

public sealed class CapturedBitmapData : SafeHandle, IVisualElement.ICapturedBitmapData
{
    public PixelFormat Format { get; }
    public AlphaFormat AlphaFormat { get; }
    public nint Data => handle;
    public PixelSize Size { get; }
    public Vector Dpi { get; }
    public int Stride { get; }

    public static CapturedBitmapData Empty => new();

    private CapturedBitmapData() : base(0, true)
    {
        Format = PixelFormat.Rgba8888;
        AlphaFormat = AlphaFormat.Premul;
        Size = new PixelSize(0, 0);
        Dpi = new Vector(0, 0);
        Stride = 0;
    }

    public CapturedBitmapData(CGImage cgImage, double scaleFactor) : base(0, true)
    {
        Format = PixelFormat.Rgba8888;
        AlphaFormat = AlphaFormat.Premul;

        var width = (int)cgImage.Width;
        var height = (int)cgImage.Height;

        Size = new PixelSize(width, height);
        Dpi = new Vector(72 * scaleFactor, 72 * scaleFactor);
        Stride = width * 4;

        SetHandle(Marshal.AllocHGlobal(Stride * height));

        using var colorSpace = CGColorSpace.CreateDeviceRGB();
        const int bitsPerComponent = 8;
        using var context = new CGBitmapContext(
            Data,
            width,
            height,
            bitsPerComponent,
            Stride,
            colorSpace,
            CGImageAlphaInfo.PremultipliedLast);

        context.DrawImage(new CGRect(0, 0, width, height), cgImage);
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(Data);
        return true;
    }

    public override bool IsInvalid => handle == 0;
}
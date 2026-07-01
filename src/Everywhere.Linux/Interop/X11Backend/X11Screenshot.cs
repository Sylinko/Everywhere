using Avalonia;
using Everywhere.Interop;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Handles screen capture and pixel format conversions.
/// </summary>
public sealed class X11Screenshot(X11Context context)
{
    public IVisualElement.ICapturedBitmapData Capture(X11Window drawable, PixelRect rect)
    {
        var xImage = Xlib.XGetImage(
            context.Display,
            drawable,
            rect.X,
            rect.Y,
            (uint)rect.Width,
            (uint)rect.Height,
            (ulong)Planes.AllPlanes,
            PixmapFormat.ZPixmap);
        if (xImage.data == IntPtr.Zero) throw new InvalidOperationException("XGetImage returned null");

        try
        {
            return new X11CapturedBitmapData(xImage);
        }
        finally
        {
            Xutil.XDestroyImage(ref xImage);
        }
    }
}
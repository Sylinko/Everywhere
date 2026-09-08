using Avalonia;
using Everywhere.Automation;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Handles screen capture and pixel format conversions.
/// </summary>
public sealed class X11Screenshot(X11Context context)
{
    /// <summary>Captures a window-local rectangle and returns bounded pixels with root-window coordinates.</summary>
    public IVisualElementCapture Capture(X11Window drawable, PixelRect rect) => context.InvokeSync(() => CaptureCore(drawable, rect));

    private IVisualElementCapture CaptureCore(X11Window drawable, PixelRect rect)
    {
        Xlib.XGetWindowAttributes(context.Display, drawable, out var attributes);
        if ((X11Native.MapState)attributes.map_state != X11Native.MapState.IsViewable) throw new InvalidOperationException("XGetImage requires a viewable window.");
        if (X11Native.XTranslateCoordinates(context.Display, drawable, context.RootWindow, 0, 0, out var originX, out var originY, out _) == 0)
            throw new InvalidOperationException("Failed to locate the capture window on its root screen.");
        Xlib.XGetWindowAttributes(context.Display, context.RootWindow, out var rootAttributes);

        // XGetImage requires a window rectangle to fit both the window and screen. It cannot
        // provide DWM-like minimized/offscreen contents; obscured pixels may still be undefined.
        rect = rect.Intersect(new PixelRect(0, 0, (int)attributes.width, (int)attributes.height))
            .Intersect(new PixelRect(-originX, -originY, (int)rootAttributes.width, (int)rootAttributes.height));
        if (rect.Width <= 0 || rect.Height <= 0) throw new InvalidOperationException("The requested region has no capturable X11 pixels.");
        var bounds = new PixelRect(checked(originX + rect.X), checked(originY + rect.Y), rect.Width, rect.Height);
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
            // The result copies/resamples pixels; only this method owns the temporary XImage.
            return new X11CapturedBitmapData(xImage, bounds);
        }
        finally
        {
            Xutil.XDestroyImage(ref xImage);
        }
    }
}
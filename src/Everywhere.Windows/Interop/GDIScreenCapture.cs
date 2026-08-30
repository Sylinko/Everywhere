using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Utilities;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Owns a top-down 32-bit GDI device-independent bitmap containing captured screen pixels.
/// </summary>
public sealed class GDIScreenCapture : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelFormat Format => PixelFormat.Bgra8888;

    /// <inheritdoc />
    public AlphaFormat AlphaFormat => AlphaFormat.Opaque;

    /// <inheritdoc />
    public nint Data => _bitmapHandle is { IsClosed: false, IsInvalid: false } ? _data : 0;

    /// <inheritdoc />
    public PixelSize Size { get; }

    /// <inheritdoc />
    public int Stride { get; }

    private GdiBitmapSafeHandle? _bitmapHandle;
    private nint _data;

    private GDIScreenCapture(GdiBitmapSafeHandle bitmapHandle, nint data, PixelSize size)
    {
        _bitmapHandle = bitmapHandle;
        _data = data;
        Size = size;
        Stride = size.Width * 4;
    }

    /// <summary>
    /// Captures the part of a physical-pixel screen rectangle that intersects the Windows virtual screen.
    /// </summary>
    /// <param name="requestedBounds">The requested rectangle in virtual-screen physical pixel coordinates.</param>
    /// <returns>An owned capture, or <see langword="null" /> when the requested rectangle does not intersect the virtual screen.</returns>
    public static unsafe GDIScreenCapture? Capture(PixelRect requestedBounds)
    {
        var virtualBounds = new PixelRect(
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN));
        var bounds = requestedBounds.Intersect(virtualBounds);
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var screenDc = PInvoke.GetDC(HWND.Null);
        if (screenDc.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to acquire the screen device context.");
        }

        var memoryDc = HDC.Null;
        HBITMAP bitmap = default;
        HGDIOBJ previousBitmap = default;
        try
        {
            memoryDc = PInvoke.CreateCompatibleDC(screenDc);
            if (memoryDc.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the capture device context.");
            }

            var bitmapInfo = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)sizeof(BITMAPINFOHEADER),
                    biWidth = bounds.Width,
                    biHeight = -bounds.Height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = (uint)BI_COMPRESSION.BI_RGB
                }
            };
            void* pixels = null;
            bitmap = PInvoke.CreateDIBSection(screenDc, &bitmapInfo, DIB_USAGE.DIB_RGB_COLORS, &pixels, default, 0);
            if (bitmap.IsNull || pixels is null)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create the capture bitmap.");
            }

            previousBitmap = PInvoke.SelectObject(memoryDc, bitmap);
            if (previousBitmap.IsNull)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to select the capture bitmap.");
            }

            if (!PInvoke.BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height, screenDc, bounds.X, bounds.Y, ROP_CODE.SRCCOPY))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to copy screen pixels into the capture bitmap.");
            }

            PInvoke.SelectObject(memoryDc, previousBitmap);
            previousBitmap = default;

            var bitmapHandle = new GdiBitmapSafeHandle(bitmap);
            bitmap = default;
            return new GDIScreenCapture(bitmapHandle, (nint)pixels, bounds.Size);
        }
        finally
        {
            if (!previousBitmap.IsNull)
            {
                PInvoke.SelectObject(memoryDc, previousBitmap);
            }

            if (!memoryDc.IsNull)
            {
                PInvoke.DeleteDC(memoryDc);
            }

            if (!bitmap.IsNull)
            {
                PInvoke.DeleteObject(bitmap);
            }

            PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _data = 0;
        DisposeHelper.DisposeToDefault(ref _bitmapHandle);
    }

    private sealed class GdiBitmapSafeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public GdiBitmapSafeHandle(HBITMAP bitmap) : base(true) => SetHandle(bitmap);

        protected override bool ReleaseHandle() => PInvoke.DeleteObject((HGDIOBJ)handle);
    }
}
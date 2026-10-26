using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Everywhere.Views;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Configures owned UI surfaces through the compositor bridge and backend input regions.
/// </summary>
public sealed partial class LinuxWindowHelper(IOverlayWindowController overlays) : IWindowHelper, IDisposable
{
    private nint _display;

    /// <inheritdoc />
    public void SetWindowProperties(
        Window window,
        WindowInputMode? inputMode = null,
        bool? hitTestVisible = null,
        WindowLayer? layer = null)
    {
        // Some transient surfaces configure their input policy in their constructor.
        // Apply the same owned-window role before their first Wayland map.
        InitializeWindow(window);
        if (inputMode is { } mode)
        {
            overlays.SetKeyboardInteractivity(window, mode == WindowInputMode.Normal);
        }

        if (hitTestVisible is { } isHitTestVisible)
        {
            overlays.SetInputRegion(window, isHitTestVisible ? null : default(PixelRect));
        }

        if (layer is { } band)
        {
            overlays.SetTopmost(window, band != WindowLayer.Normal);
        }

        if (layer is >= WindowLayer.Overlay && !window.ShowActivated && !window.IsHitTestVisible &&
            double.IsFinite(window.Width) && double.IsFinite(window.Height))
        {
            overlays.SetOverlayBounds(
                window,
                new PixelRect(
                    window.Position,
                    new PixelSize(
                        (int)Math.Round(window.Width * window.DesktopScaling),
                        (int)Math.Round(window.Height * window.DesktopScaling))));
        }
    }

    /// <inheritdoc />
    public bool TryPrepareInput(Window window) => false;

    /// <inheritdoc />
    public IWindowInputCapture? TryCaptureInput(Window window) => null;

    /// <inheritdoc />
    public void RaiseWindow(Window window) => overlays.Raise(window);

    /// <inheritdoc />
    public bool GetEffectiveVisible(Window window) => window.IsVisible && window.WindowState != WindowState.Minimized;

    /// <inheritdoc />
    public bool? IsRegionCovered(Window window, PixelRect bounds) => null;

    /// <inheritdoc />
    public void SetCloaked(Window window, bool cloaked)
    {
        if (cloaked)
        {
            window.Hide();
        }
        else
        {
            window.Show();
        }
    }

    /// <inheritdoc />
    public bool AnyModelDialogOpened(Window window) => window.OwnedWindows.Any(item => item.IsVisible);

    /// <inheritdoc />
    public void RequestUserAttention(Window window)
    {
        if (!TryGetX11Window(window, out var handle))
        {
            return;
        }

        var hints = XGetWMHints(_display, handle);
        if (hints == 0)
        {
            return;
        }
        try
        {
            Marshal.WriteInt64(hints, Marshal.ReadInt64(hints) | 256);
            XSetWMHints(_display, handle, hints);
            XFlush(_display);
        }
        finally
        {
            XFree(hints);
        }
    }

    /// <inheritdoc />
    public bool BringToForeground(Window window)
    {
        overlays.Activate(window);
        return window.IsActive;
    }

    /// <inheritdoc />
    public double SetCornerRadius(Window window, double radius) => 0;

    /// <inheritdoc />
    public void InitializeWindow(Window window)
    {
        if (window is TextSelectionToolbarWindow)
        {
            overlays.AttachOverlayWindow(window, OverlayRole.SelectionToolbar);
        }
        else if (window is ChatWindow)
        {
            overlays.AttachOverlayWindow(window, OverlayRole.FloatingWindow);
        }
        else if (window is VisualElementOverlayWindow)
        {
            overlays.AttachOverlayWindow(window, OverlayRole.PassiveHighlight);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        var display = Interlocked.Exchange(ref _display, 0);
        if (display != 0)
        {
            XCloseDisplay(display);
        }
    }

    private bool TryGetX11Window(Window window, out nint handle)
    {
        handle = 0;
        if (window.TryGetPlatformHandle() is not { Handle: not 0, HandleDescriptor: "XID" } platformHandle)
        {
            return false;
        }

        if (_display == 0)
        {
            _display = XOpenDisplay(0);
        }

        if (_display == 0)
        {
            return false;
        }
        handle = platformHandle.Handle;
        return true;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial nint XGetWMHints(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XSetWMHints(nint display, nint window, nint hints);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint pointer);
}
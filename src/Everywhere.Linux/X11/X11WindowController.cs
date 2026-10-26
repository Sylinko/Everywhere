using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.X11;

/// <summary>
/// Uses Avalonia's ordinary X11 placement/activation and the old Linux design's
/// native input shape and WM_HINTS. No Hyprland rules or Wayland proxies are used.
/// Text-selection toolbars and global input observation remain outside this starter.
/// </summary>
public sealed partial class X11WindowController : IOverlayWindowController, IDisposable
{
    private nint _display;
    private readonly HashSet<Window> _windows = [];

    /// <inheritdoc />
    public void AttachOverlayWindow(Window window, OverlayRole role)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (role == OverlayRole.SelectionToolbar)
        {
            throw new NotSupportedException("The X11 text-selection toolbar has not been ported.");
        }
        if (_windows.Add(window))
        {
            window.Closed += WindowClosed;
        }
        if (role == OverlayRole.FloatingWindow)
        {
            return;
        }
        window.WindowDecorations = WindowDecorations.None;
        window.ShowInTaskbar = false;
        SetTopmost(window, true);
        SetKeyboardInteractivity(window, role == OverlayRole.ScreenSelector);
        SetInputRegion(window, role == OverlayRole.ScreenSelector ? null : default(PixelRect));
    }

    /// <inheritdoc />
    public void SetOverlayBounds(Window window, PixelRect logicalBounds)
    {
        Dispatcher.UIThread.VerifyAccess();
        window.Width = logicalBounds.Width / window.DesktopScaling;
        window.Height = logicalBounds.Height / window.DesktopScaling;
        window.Position = logicalBounds.Position;
    }

    /// <inheritdoc />
    public void SetInputRegion(Window window, PixelRect? clickableRegion)
    {
        var handle = GetHandle(window);
        window.IsHitTestVisible = clickableRegion is null || clickableRegion.Value.Width > 0 && clickableRegion.Value.Height > 0;
        if (clickableRegion is null)
        {
            XShapeCombineMask(_display, handle, 2, 0, 0, 0, 0);
        }
        else if (clickableRegion is { Width: > 0, Height: > 0 } rectangle)
        {
            var native = new NativeRectangle
            {
                X = checked((short)rectangle.X),
                Y = checked((short)rectangle.Y),
                Width = checked((ushort)rectangle.Width),
                Height = checked((ushort)rectangle.Height),
            };
            XShapeCombineRectangles(_display, handle, 2, 0, 0, in native, 1, 0, 0);
        }
        else
        {
            XShapeCombineRectangles(_display, handle, 2, 0, 0, 0, 0, 0, 0);
        }
        XFlush(_display);
    }

    /// <inheritdoc />
    public void SetKeyboardInteractivity(Window window, bool canAcceptFocus)
    {
        var handle = GetHandle(window);
        window.ShowActivated = canAcceptFocus;
        var hints = XGetWMHints(_display, handle);
        if (hints == 0)
        {
            throw new NotSupportedException("The X11 window has no WM_HINTS input policy.");
        }
        try
        {
            Marshal.WriteIntPtr(hints, (nint)(Marshal.ReadIntPtr(hints).ToInt64() | 1));
            Marshal.WriteInt32(hints, IntPtr.Size, canAcceptFocus ? 1 : 0);
            XSetWMHints(_display, handle, hints);
            XFlush(_display);
        }
        finally
        {
            XFree(hints);
        }
    }

    /// <inheritdoc />
    public void SetTopmost(Window window, bool isTopmost) => window.Topmost = isTopmost;

    /// <inheritdoc />
    public void Raise(Window window)
    {
        var handle = GetHandle(window);
        XRaiseWindow(_display, handle);
        XFlush(_display);
    }

    /// <inheritdoc />
    public void Activate(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        window.Activate();
    }

    /// <inheritdoc />
    public async Task WaitForHiddenSurfacesAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var connection = new X11Connection();
            var hasMappedHiddenWindow = false;
            foreach (var window in _windows.Where(item => !item.IsVisible))
            {
                if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID", Handle: not 0 } handle)
                {
                    continue;
                }
                try
                {
                    hasMappedHiddenWindow |= connection.GetClientBounds(checked((uint)handle.Handle)) is not null;
                }
                catch (InvalidOperationException)
                {
                    // Closed windows have no remaining drawable.
                }
            }
            if (!hasMappedHiddenWindow)
            {
                return;
            }
            await Task.Delay(16, deadline.Token);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var window in _windows)
        {
            window.Closed -= WindowClosed;
        }
        _windows.Clear();
        if (_display != 0)
        {
            XCloseDisplay(_display);
        }
        _display = 0;
    }

    private void WindowClosed(object? sender, EventArgs args)
    {
        if (sender is not Window window)
        {
            return;
        }
        window.Closed -= WindowClosed;
        _windows.Remove(window);
    }

    private nint GetHandle(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (window.TryGetPlatformHandle() is not { HandleDescriptor: "XID", Handle: not 0 } handle)
        {
            throw new InvalidOperationException("The window is not using Avalonia's X11 backend.");
        }
        if (_display == 0)
        {
            _display = XOpenDisplay(0);
        }
        if (_display == 0)
        {
            throw new InvalidOperationException("The selected X11 display is unavailable.");
        }
        return handle.Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle
    {
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XRaiseWindow(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial nint XGetWMHints(nint display, nint window);

    [LibraryImport("libX11.so.6")]
    private static partial int XSetWMHints(nint display, nint window, nint hints);

    [LibraryImport("libX11.so.6")]
    private static partial int XFree(nint pointer);

    [LibraryImport("libXext.so.6")]
    private static partial void XShapeCombineMask(nint display, nint window, int kind, int x, int y, nint mask, int operation);

    [LibraryImport("libXext.so.6")]
    private static partial void XShapeCombineRectangles(
        nint display,
        nint window,
        int kind,
        int x,
        int y,
        nint rectangles,
        int count,
        int operation,
        int ordering);

    [LibraryImport("libXext.so.6", EntryPoint = "XShapeCombineRectangles")]
    private static partial void XShapeCombineRectangles(
        nint display,
        nint window,
        int kind,
        int x,
        int y,
        in NativeRectangle rectangle,
        int count,
        int operation,
        int ordering);
}
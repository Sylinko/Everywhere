using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Wayland;

/// <summary>
/// Leaves ordinary toplevel rendering, focus and text-input protocols to Avalonia
/// and the desktop. Global positioning, passive surfaces and stacking require a
/// compositor-specific backend and are deliberately unavailable in this starter.
/// </summary>
public sealed class StandardWaylandWindowController : IOverlayWindowController
{
    /// <inheritdoc />
    public void AttachOverlayWindow(Window window, OverlayRole role)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (role != OverlayRole.FloatingWindow)
        {
            throw Unavailable();
        }
    }

    /// <inheritdoc />
    public void SetOverlayBounds(Window window, PixelRect logicalBounds) => throw Unavailable();

    /// <inheritdoc />
    public void SetInputRegion(Window window, PixelRect? clickableRegion)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (clickableRegion is not null)
        {
            throw Unavailable();
        }
        window.IsHitTestVisible = true;
    }

    /// <inheritdoc />
    public void SetKeyboardInteractivity(Window window, bool canAcceptFocus)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (!canAcceptFocus)
        {
            throw Unavailable();
        }
        window.ShowActivated = true;
    }

    /// <inheritdoc />
    public void SetTopmost(Window window, bool isTopmost)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (isTopmost)
        {
            throw Unavailable();
        }
        window.Topmost = false;
    }

    /// <inheritdoc />
    public void Raise(Window window) => throw Unavailable();

    /// <inheritdoc />
    public void Activate(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        window.Activate();
    }

    /// <inheritdoc />
    public Task WaitForHiddenSurfacesAsync(CancellationToken cancellationToken) => Task.FromException(Unavailable());

    private static NotSupportedException Unavailable() => new("Compositor-controlled overlays are not implemented for this Wayland desktop.");
}
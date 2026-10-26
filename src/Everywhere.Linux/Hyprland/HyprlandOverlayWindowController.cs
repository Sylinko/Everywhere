using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Everywhere.Linux.Desktop;
using Everywhere.Linux.Platform;

namespace Everywhere.Linux.Hyprland;

/// <summary>
/// Applies common presentation roles through the process-owned Hyprland window bridge.
/// </summary>
public sealed class HyprlandOverlayWindowController : IOverlayWindowController
{
    /// <inheritdoc />
    public void AttachOverlayWindow(Window window, OverlayRole role)
    {
        var state = GetState(window);
        state.IsFloating = true;
        state.IsAnchoredPopup = role == OverlayRole.SelectionToolbar;
        if (role is OverlayRole.PassiveHighlight or OverlayRole.ScreenSelector)
        {
            window.WindowDecorations = WindowDecorations.None;
            window.ShowInTaskbar = false;
            SetTopmost(window, true);
            SetKeyboardInteractivity(window, role == OverlayRole.ScreenSelector);
            SetInputRegion(window, role == OverlayRole.ScreenSelector ? null : default(PixelRect));
        }
    }

    /// <inheritdoc />
    public void SetOverlayBounds(Window window, PixelRect logicalBounds)
    {
        GetState(window).SetOverlayBounds(logicalBounds);
    }

    /// <inheritdoc />
    public void SetInputRegion(Window window, PixelRect? clickableRegion)
    {
        // Avalonia 12.1.3's UI-side surface proxy supports whole-surface input or
        // an empty wl_region. It marshals wl_surface_set_input_region to the owning
        // Wayland worker and reapplies it on reconnect. Do not touch wl_surface from
        // the UI thread, or advertise a finite region that the backend cannot honor.
        if (clickableRegion is { Width: > 0, Height: > 0 })
        {
            throw new NotSupportedException("Avalonia 12.1.3 supports full-surface or empty input regions, not partial rectangles.");
        }

        var state = GetState(window);
        var isHitTestVisible = clickableRegion is null;
        window.IsHitTestVisible = isHitTestVisible;
        state.IsHitTestVisible = isHitTestVisible;
        state.RefreshInputPolicy();
    }

    /// <inheritdoc />
    public void SetKeyboardInteractivity(Window window, bool canAcceptFocus)
    {
        var state = GetState(window);
        window.ShowActivated = canAcceptFocus;
        state.CanActivate = canAcceptFocus;
        state.RefreshInputPolicy();
    }

    /// <inheritdoc />
    public void SetTopmost(Window window, bool isTopmost)
    {
        var state = GetState(window);
        window.Topmost = isTopmost;
        state.IsTopmost = isTopmost;
    }

    /// <inheritdoc />
    public void Raise(Window window) => GetState(window).Raise();

    /// <inheritdoc />
    public void Activate(Window window) => GetState(window).Activate();

    /// <inheritdoc />
    public Task WaitForHiddenSurfacesAsync(CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        return GetPlatform().WaitForHiddenSurfacesAsync(cancellationToken);
    }

    private static HyprlandWindowState GetState(Window window)
    {
        Dispatcher.UIThread.VerifyAccess();
        return window.PlatformImpl?.TryGetFeature<HyprlandWindowState>()
            ?? throw new InvalidOperationException("The Hyprland windowing bridge is not initialized.");
    }

    private static HyprlandWindowingPlatform GetPlatform()
    {
        return AvaloniaLocator.Current.GetRequiredService<IWindowingPlatform>() as HyprlandWindowingPlatform
            ?? throw new InvalidOperationException("The Hyprland windowing bridge is not initialized.");
    }
}
using Avalonia;
using Avalonia.Controls;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Configures process-owned surfaces on Avalonia's UI thread, before their first map
/// when possible. Policy setters enqueue native changes synchronously, matching
/// IWindowHelper; compositor acknowledgements belong to IWindowPresentation and
/// WaitForHiddenSurfacesAsync, rather than blocking the UI on asynchronous setters.
/// </summary>
public interface IOverlayWindowController
{
    /// <summary>
    /// Applies a window's role without mapping it or requesting activation.
    /// </summary>
    void AttachOverlayWindow(Window window, OverlayRole role);

    /// <summary>
    /// Places a surface in global logical desktop coordinates.
    /// </summary>
    void SetOverlayBounds(Window window, PixelRect logicalBounds);

    /// <summary>
    /// Sets a surface-local logical input region. Null accepts the entire surface;
    /// an empty rectangle passes all pointer input through. Unsupported finite
    /// regions must throw rather than silently intercept input outside the region.
    /// </summary>
    void SetInputRegion(Window window, PixelRect? clickableRegion);

    /// <summary>
    /// Controls whether the surface may acquire keyboard focus; does not itself activate it.
    /// </summary>
    void SetKeyboardInteractivity(Window window, bool canAcceptFocus);

    /// <summary>
    /// Sets owned-window stacking policy.
    /// </summary>
    void SetTopmost(Window window, bool isTopmost);

    /// <summary>
    /// Raises the surface without requesting keyboard focus.
    /// </summary>
    void Raise(Window window);

    /// <summary>
    /// Requests focus when permitted by its input policy.
    /// </summary>
    void Activate(Window window);

    /// <summary>
    /// Waits until previous owned surfaces are absent from subsequent captures.
    /// </summary>
    Task WaitForHiddenSurfacesAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Presentation intent, independent of a compositor's rules or surface protocol.
/// </summary>
public enum OverlayRole
{
    /// <summary>
    /// A normal floating application window.
    /// </summary>
    FloatingWindow,

    /// <summary>
    /// A positioned toolbar that may switch between passive and editable states.
    /// </summary>
    SelectionToolbar,

    /// <summary>
    /// A pointer-transparent, non-activating highlight.
    /// </summary>
    PassiveHighlight,

    /// <summary>
    /// An interactive full-output selection surface.
    /// </summary>
    ScreenSelector,
}
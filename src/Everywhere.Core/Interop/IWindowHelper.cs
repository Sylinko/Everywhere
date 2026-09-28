using Avalonia.Controls;

namespace Everywhere.Interop;

/// <summary>
/// Identifies sparse native z-order bands. Values within a band may be offset to request a stable
/// relative order on platforms that expose numeric window levels; other platforms preserve the band only.
/// </summary>
public enum WindowLayer
{
    /// <summary>A normal application window.</summary>
    Normal = 0,

    /// <summary>An ordinary always-on-top application window.</summary>
    Topmost = 100,

    /// <summary>A non-activating system-interaction overlay above ordinary topmost windows.</summary>
    Overlay = 1000
}

/// <summary>
/// Provides helper methods for interacting with application windows.
/// </summary>
public interface IWindowHelper
{
    /// <summary>
    /// Updates selected native input and z-order properties of a window without suppressing normal activation or focus notifications.
    /// </summary>
    /// <param name="window">The target window.</param>
    /// <param name="focusable">Whether ordinary user interaction may activate the window and give it keyboard focus, or <see langword="null" /> to leave it unchanged.</param>
    /// <param name="hitTestVisible">Whether the window receives pointer input, or <see langword="null" /> to leave it unchanged.</param>
    /// <param name="layer">The requested native window layer, or <see langword="null" /> to leave it unchanged.</param>
    void SetWindowProperties(Window window, bool? focusable = null, bool? hitTestVisible = null, WindowLayer? layer = null);

    /// <summary>
    /// Raises the window within its configured native layer without activating it.
    /// </summary>
    /// <param name="window">The target window.</param>
    void RaiseWindow(Window window);

    /// <summary>
    /// Get whether the window is effectively visible (taking into account cloaking and other factors).
    /// </summary>
    /// <param name="window"></param>
    /// <returns></returns>
    bool GetEffectiveVisible(Window window);

    /// <summary>
    /// Checks representative points in a screen-space region against the native window stack.
    /// Returns null when the current platform cannot perform the check reliably.
    /// </summary>
    bool? IsRegionCovered(Window window, PixelRect bounds);

    /// <summary>
    /// Set whether the window is cloaked (invisible and non-interactive, without any animation).
    /// </summary>
    /// <param name="window"></param>
    /// <param name="cloaked"></param>
    void SetCloaked(Window window, bool cloaked);

    /// <summary>
    /// Get whether any dialog is opened on the given window. (e.g. MessageBox, OpenFileDialog, etc.)
    /// </summary>
    /// <param name="window"></param>
    /// <returns></returns>
    bool AnyModelDialogOpened(Window window);

    /// <summary>
    /// Request user attention to the window (e.g. flash taskbar icon, bounce dock icon).
    /// </summary>
    /// <param name="window"></param>
    void RequestUserAttention(Window window);

    /// <summary>
    /// Sets the requested native window corner radius in Avalonia logical pixels.
    /// </summary>
    /// <param name="window">The target window.</param>
    /// <param name="radius">The requested radius.</param>
    /// <returns>The actual applied corner radius</returns>
    double SetCornerRadius(Window window, double radius);

    /// <summary>
    /// Initialize the window properties by its type
    /// </summary>
    /// <param name="window"></param>
    void InitializeWindow(Window window);
}
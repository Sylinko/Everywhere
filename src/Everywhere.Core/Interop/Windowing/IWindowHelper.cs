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
    /// <remarks>
    /// Configure the input mode on the UI thread before logically focusing an editor. Changing the
    /// input mode while an editor is focused requires clearing and restoring its logical focus.
    /// </remarks>
    /// <param name="window">The target window.</param>
    /// <param name="inputMode">The native activation and logical input policy, or <see langword="null" /> to leave it unchanged.</param>
    /// <param name="hitTestVisible">Whether the window receives pointer input, or <see langword="null" /> to leave it unchanged.</param>
    /// <param name="layer">The requested native window layer, or <see langword="null" /> to leave it unchanged.</param>
    void SetWindowProperties(Window window, WindowInputMode? inputMode = null, bool? hitTestVisible = null, WindowLayer? layer = null);

    /// <summary>
    /// Prepares a native input host without capturing input or creating an editor context.
    /// Call on the UI thread when showing a non-activating text-input window in its passive mode.
    /// </summary>
    /// <remarks>
    /// Configure <see cref="WindowInputMode.NonActivatingTextInput" /> first. The later capture must
    /// originate from a separate user interaction, after normal message processing has resumed.
    /// Preparation records the foreground source window; a changed source invalidates capture.
    /// Returns false when the platform or native input route is unavailable. Never activates the window.
    /// </remarks>
    bool TryPrepareInput(Window window);

    /// <summary>
    /// Captures input for a visible, previously prepared window without changing the foreground window.
    /// Call on the UI thread after attaching and laying out the editor, then logically focus that editor.
    /// </summary>
    /// <remarks>
    /// Returns null when unsupported, unprepared, unavailable or already captured. This method does not
    /// initialize a native host, pump messages, or retry. Hiding, minimizing or closing the window ends
    /// capture; disposing the returned lease ends it explicitly. The caller owns the lease.
    /// </remarks>
    IWindowInputCapture? TryCaptureInput(Window window);

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
    /// Makes the window the foreground window, so it can receive keyboard input.
    /// </summary>
    /// <remarks>
    /// <see cref="Window.Activate"/> is not sufficient when the request originates from a
    /// non-activating overlay: the application never became the foreground process, and Windows refuses
    /// a foreground change from a process that neither owns the foreground window nor received the last
    /// activating input. This performs whatever platform-specific escalation is required.
    /// </remarks>
    /// <param name="window"></param>
    /// <returns>
    /// True if the window is the foreground window afterwards. The platform may refuse the change, and a
    /// caller that has just hidden or is about to hide another window needs to know rather than assume.
    /// </returns>
    bool BringToForeground(Window window);

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
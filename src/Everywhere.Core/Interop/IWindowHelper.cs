using Avalonia.Controls;

namespace Everywhere.Interop;

/// <summary>
/// Provides helper methods for interacting with application windows.
/// </summary>
public interface IWindowHelper
{
    /// <summary>
    /// Set whether the window is focusable or not.
    /// </summary>
    /// <param name="window"></param>
    /// <param name="focusable"></param>
    void SetFocusable(Window window, bool focusable);

    /// <summary>
    /// Set whether the window is hit-test visible (interactive) or not.
    /// </summary>
    /// <param name="window"></param>
    /// <param name="visible"></param>
    void SetHitTestVisible(Window window, bool visible);

    /// <summary>
    /// Get whether the window is effectively visible (taking into account cloaking and other factors).
    /// </summary>
    /// <param name="window"></param>
    /// <returns></returns>
    bool GetEffectiveVisible(Window window);

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
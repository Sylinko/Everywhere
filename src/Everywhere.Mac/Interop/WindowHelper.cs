using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Everywhere.Interop;
using Everywhere.Views;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

public sealed class WindowHelper : IWindowHelper
{
    private int OpenedWindowCount
    {
        get;
        set
        {
            value = Math.Max(0, value);
            if (value == field) return;

            // changing activation policy
            if (field == 0)
            {
                // first window opened
                AppDelegate.IsVisibleInDock = true;
            }
            else if (value == 0)
            {
                // last window closed
                AppDelegate.IsVisibleInDock = false;
            }

            field = value;
        }
    }

    private bool IsChatWindowCloaked
    {
        set
        {
            if (value == field) return;
            field = value;
            if (value) OpenedWindowCount--;
            else OpenedWindowCount++;
        }
    }

    public WindowHelper()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            OpenedWindowCount = desktop.Windows.Count;
        }

        Window.WindowOpenedEvent.AddClassHandler<Window>(HandleWindowOpened, handledEventsToo: true);
        Window.WindowClosedEvent.AddClassHandler<Window>(HandleWindowClosed, handledEventsToo: true);
    }

    private void HandleWindowOpened(Window window, RoutedEventArgs args)
    {
        if (window is not ChatWindow) OpenedWindowCount++;
    }

    private void HandleWindowClosed(Window window, RoutedEventArgs args)
    {
        if (window is not ChatWindow) OpenedWindowCount--;
    }

    /// <summary>
    /// Sets whether the window can become the key window (i.e., receive keyboard focus).
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="focusable">True to allow focus, false to prevent it.</param>
    public void SetFocusable(Window window, bool focusable)
    {
        // We need to NonactivatingPanel, but only NSPanel supports that.
        // So we cannot implement currently.
    }

    /// <summary>
    /// Sets whether the window is transparent to mouse events.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="visible">True to make it receive mouse events, false to let them pass through.</param>
    public void SetHitTestVisible(Window window, bool visible)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        // This is the direct equivalent of WS_EX_TRANSPARENT on Windows.
        nativeWindow.IgnoresMouseEvents = !visible;

        // Special handling to ensure it remains interactive in full screen mode.
        nativeWindow.CollectionBehavior |=
            NSWindowCollectionBehavior.CanJoinAllSpaces |
            NSWindowCollectionBehavior.FullScreenAuxiliary;
        nativeWindow.CollectionBehavior &=
            ~(NSWindowCollectionBehavior.FullScreenPrimary |
                NSWindowCollectionBehavior.Managed);

        if (window is ScreenSelectionMaskWindow or VisualElementEffectWindow)
        {
            nativeWindow.Level = NSWindowLevel.ScreenSaver + 1;
        }
    }

    /// <summary>
    /// Gets the effective visibility of the window, considering its occlusion state.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <returns>True if the window is truly visible on screen.</returns>
    public bool GetEffectiveVisible(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return window.IsVisible;

        // NSWindow.IsVisible checks if the window is on-screen.
        // NSWindow.OcclusionState tells us if it's obscured by other windows.
        // A window is effectively visible if it's marked as visible and not fully occluded.
        var isVisible = nativeWindow.IsVisible;
        var isOccluded = (nativeWindow.OcclusionState & NSWindowOcclusionState.Visible) == 0;

        return isVisible && !isOccluded;
    }

    /// <summary>
    /// Hides or shows the window from the user's view without destroying it.
    /// macOS doesn't have a direct "Cloak" concept like DWM.
    /// The closest equivalent is hiding the window and managing its space behavior.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="cloaked">True to hide (cloak), false to show (uncloak).</param>
    public void SetCloaked(Window window, bool cloaked)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        if (window is ChatWindow)
        {
            // For ChatWindow, we might want to ensure it can appear on all spaces and in full screen mode.
            nativeWindow.CollectionBehavior =
                NSWindowCollectionBehavior.CanJoinAllSpaces |
                NSWindowCollectionBehavior.FullScreenAuxiliary;

            // Chat window will not be closed, so hide/show is treated as close/open for counting purposes.
            IsChatWindowCloaked = cloaked;
        }

        if (cloaked)
        {
            // Hide the window and ensure it's not in the window cycle (Cmd+Tab).
            nativeWindow.CollectionBehavior |= NSWindowCollectionBehavior.IgnoresCycle;

            // Animate the hiding to avoid flicker
            NSAnimationContext.BeginGrouping();
            NSAnimationContext.CurrentContext.Duration = 0;
            window.Hide();
            NSAnimationContext.EndGrouping();
        }
        else
        {
            // Show the window, make it the frontmost, and restore its cycle behavior.
            window.Show();
            nativeWindow.CollectionBehavior &= ~NSWindowCollectionBehavior.IgnoresCycle;
            nativeWindow.MakeKeyAndOrderFront(null);

            // Make sure it gets an input focus.
#pragma warning disable CA1422
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
        }
    }

    /// <summary>
    /// Checks if the window has any open modal dialogs.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <returns>True if a modal dialog is active for this window.</returns>
    public bool AnyModelDialogOpened(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return false;

        // NSApplication.SharedApplication.ModalWindow returns the current modal window.
        // We check if that modal window's sheet parent is our window.
        var modalWindow = NSApplication.SharedApplication.ModalWindow;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (modalWindow is not null)
        {
            // If a sheet is presented, its Window is the sheet itself, and SheetParent is the owner.
            if (modalWindow.SheetParent.Equals(nativeWindow))
            {
                return true;
            }
        }

        return false;
    }

    public void RequestUserAttention(Window window)
    {
        NSApplication.SharedApplication.RequestUserAttention(NSRequestUserAttentionType.InformationalRequest);
    }

    public void InitializeWindow(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        if (window is ChatWindow)
        {
            // for ChatWindow, disallow closing
            nativeWindow.StyleMask &= ~NSWindowStyle.Closable;
        }
        else
        {
            // for other windows, disable fullscreen
            nativeWindow.CollectionBehavior |= NSWindowCollectionBehavior.FullScreenNone;
            nativeWindow.CollectionBehavior &= ~(NSWindowCollectionBehavior.FullScreenPrimary | NSWindowCollectionBehavior.FullScreenAuxiliary);
        }
    }

    /// <summary>
    /// Gets the native NSWindow from an Avalonia Window.
    /// </summary>
    private static NSWindow? GetNativeWindow(Window window)
    {
        return window.TryGetPlatformHandle()?.Handle is { } handle ? Runtime.GetNSObject<NSWindow>(handle) : null;
    }
}
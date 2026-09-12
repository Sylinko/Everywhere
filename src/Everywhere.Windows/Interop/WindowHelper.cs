using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Interop;
using Everywhere.Views;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Reference: Powertoys
/// </summary>
public sealed class WindowHelper : IWindowHelper
{
    public void SetFocusable(Window window, bool focusable)
    {
        if (focusable)
        {
            Win32Properties.RemoveWindowStylesCallback(window, WindowStylesCallback);
            Win32Properties.RemoveWndProcHookCallback(window, WndProcHookCallback);
        }
        else
        {
            Win32Properties.AddWindowStylesCallback(window, WindowStylesCallback);
            Win32Properties.AddWndProcHookCallback(window, WndProcHookCallback);
        }

        if (window.TryGetPlatformHandle() is { } handle)
        {
            var exStyle = PInvoke.GetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

            if (focusable)
            {
                exStyle &= ~((int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
            }
            else
            {
                exStyle |= (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
            }

            PInvoke.SetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);
        }

        static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle)
        {
            return (style, exStyle | (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE | (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);
        }

        static IntPtr WndProcHookCallback(IntPtr hWnd, uint msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            // handle and block all activate messages
            // if (msg is not (>= (uint)WINDOW_MESSAGE.WM_MOUSEMOVE and <= (uint)WINDOW_MESSAGE.WM_XBUTTONDBLCLK or (uint)WINDOW_MESSAGE.WM_NCHITTEST))
            //     Console.WriteLine($"{(WINDOW_MESSAGE)msg}\t{wparam}\t{lparam}");
            switch (msg)
            {
                case (uint)WINDOW_MESSAGE.WM_MOUSEACTIVATE:
                    handled = true;
                    return 3; // MA_NOACTIVATE;

                case (uint)WINDOW_MESSAGE.WM_NCACTIVATE:
                    // Must return TRUE, not FALSE. When wParam is FALSE the window is being deactivated,
                    // and returning FALSE tells the system to *prevent* that change: the window then can
                    // never be deactivated and holds the foreground indefinitely. A non-activating window
                    // that is also hit-testable can become the foreground window when clicked, so refusing
                    // deactivation strands the foreground on a window that cannot take keyboard focus, and
                    // no window receives input afterwards. The intent of a non-focusable window is to avoid
                    // *taking* focus, not to refuse giving it up.
                    handled = true;
                    return 1;

                case (uint)WINDOW_MESSAGE.WM_ACTIVATE:
                case (uint)WINDOW_MESSAGE.WM_SETFOCUS:
                case (uint)WINDOW_MESSAGE.WM_KILLFOCUS:
                case (uint)WINDOW_MESSAGE.WM_ACTIVATEAPP:
                    handled = true;
                    return IntPtr.Zero;
                default:
                    return IntPtr.Zero;
            }
        }
    }

    public void SetHitTestVisible(Window window, bool visible)
    {
        if (visible)
        {
            Win32Properties.RemoveWindowStylesCallback(window, WindowStylesCallback);
        }
        else
        {
            Win32Properties.AddWindowStylesCallback(window, WindowStylesCallback);
        }

        if (window.TryGetPlatformHandle() is { } handle)
        {
            var style = PInvoke.GetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            var exStyle = PInvoke.GetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);

            if (visible)
            {
                style &= ~(int)WINDOW_STYLE.WS_DISABLED;
                PInvoke.SetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);

                exStyle &= ~((int)WINDOW_EX_STYLE.WS_EX_LAYERED | (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
                PInvoke.SetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);
            }
            else
            {
                style |= (int)WINDOW_STYLE.WS_DISABLED;
                PInvoke.SetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);

                exStyle |= (int)WINDOW_EX_STYLE.WS_EX_LAYERED | (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
                PInvoke.SetWindowLong((HWND)handle.Handle, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle);
                PInvoke.SetLayeredWindowAttributes((HWND)handle.Handle, new COLORREF(), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
            }
        }

        static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle)
        {
            return
            (
                style | (uint)WINDOW_STYLE.WS_DISABLED,
                exStyle | (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | (uint)WINDOW_EX_STYLE.WS_EX_LAYERED | (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT
            );
        }
    }

    public bool GetEffectiveVisible(Window window)
    {
        var isVisible = window.IsVisible;

        if (window.TryGetPlatformHandle() is not { } handle) return isVisible;

        unsafe
        {
            // We need to check if our window is cloaked or not. A cloaked window is still
            // technically visible, because SHOW/HIDE != iconic (minimized) != cloaked
            // (these are all separate states)
            long attr = 0;
            PInvoke.DwmGetWindowAttribute((HWND)handle.Handle, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &attr, sizeof(long));
            if (attr == 1 /* DWM_CLOAKED_APP */)
            {
                isVisible = false;
            }
        }

        return isVisible;
    }

    public void SetCloaked(Window window, bool cloaked)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;
        var hWnd = (HWND)handle.Handle;

        if (cloaked)
        {
            // We must first hide our Avalonia window, otherwise Avalonia's focus state will get confused
            window.Hide();

            Cloak(hWnd);
        }
        else
        {
            // Remember, IsIconic == "minimized", which is entirely different state
            // from "show/hide"
            // If we're currently minimized, restore us first, before we reveal
            // our window. Otherwise, we'd just be showing a minimized window -
            // which would remain not visible to the user.
            if (PInvoke.IsIconic(hWnd))
            {
                // Make sure our HWND is cloaked before any possible window manipulations
                Cloak(hWnd);

                PInvoke.ShowWindow(hWnd, SHOW_WINDOW_CMD.SW_RESTORE);
            }

            // Once we're done, uncloak to avoid all animations
            Uncloak(hWnd);

            // Just to be sure, SHOW our hwnd.
            window.Show();

            window.Activate();
        }
    }

    public bool BringToForeground(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle)
        {
            window.Activate();
            return false;
        }

        var hWnd = (HWND)handle.Handle;

        // Windows only grants a foreground change to a process that already owns the foreground window or
        // received the last activating input. A click on a WS_EX_NOACTIVATE overlay does not qualify, so
        // SetForegroundWindow alone is silently ignored and the window would appear but stay unfocused.
        // Briefly sharing an input queue with the current foreground thread makes the call succeed; this
        // is the same AttachThreadInput approach already used to focus native elements.
        var foregroundHwnd = PInvoke.GetForegroundWindow();
        var foregroundThreadId = foregroundHwnd == HWND.Null
            ? 0
            : PInvoke.GetWindowThreadProcessId(foregroundHwnd, out _);
        var currentThreadId = PInvoke.GetCurrentThreadId();
        var attached = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attached = PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            window.Activate();
            PInvoke.SetForegroundWindow(hWnd);

            // Establish the keyboard focus window explicitly. Taking the foreground does not reliably do
            // this when the previous foreground window was a non-activating overlay of our own process:
            // the thread ends up with a foreground window but no focus window, which leaves IME with
            // nothing to attach its composition and candidate windows to, and leaves Avalonia without the
            // activation it needs to route input to controls.
            PInvoke.SetFocus(hWnd);

            // SetForegroundWindow's return value is unreliable, so confirm against the actual state.
            return PInvoke.GetForegroundWindow() == hWnd;
        }
        finally
        {
            if (attached)
            {
                PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    public bool AnyModelDialogOpened(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return false;
        var ownerHwnd = (HWND)handle.Handle;
        var dialogFound = false;

        // This is a quick check. When a modal dialog is open, its owner window is usually disabled.
        // If the window is still enabled, then it's likely that there's no modal dialog.
        if (PInvoke.IsWindowEnabled(ownerHwnd))
        {
            return false;
        }

        // Enumerate all top-level windows to find any owned by our window.
        PInvoke.EnumWindows(
            (hwnd, _) =>
            {
                if (PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != ownerHwnd ||
                    !PInvoke.IsWindowVisible(hwnd) ||
                    !PInvoke.IsWindowEnabled(hwnd)) return true;

                dialogFound = true;
                return false;
            },
            0);

        return dialogFound;
    }

    private static void Cloak(HWND hWnd)
    {
        bool wasCloaked;
        unsafe
        {
            BOOL value = true;
            var hr = PInvoke.DwmSetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
            wasCloaked = hr.Succeeded;
        }

        if (wasCloaked)
        {
            // Because we're only cloaking the window, bury it at the bottom in case something can
            // see it - e.g. some accessibility helper (note: this also removes the top-most status).
            PInvoke.SetWindowPos(hWnd, HWND.HWND_BOTTOM, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        }

    }

    private unsafe static void Uncloak(HWND hWnd)
    {
        BOOL value = false;
        PInvoke.DwmSetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &value, (uint)sizeof(BOOL));
    }

    public unsafe void RequestUserAttention(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;

        var info = new FLASHWINFO
        {
            cbSize = (uint)sizeof(FLASHWINFO),
            hwnd = (HWND)handle.Handle,
            dwFlags = FLASHWINFO_FLAGS.FLASHW_TRAY | FLASHWINFO_FLAGS.FLASHW_TIMERNOFG,
            uCount = uint.MaxValue,
            dwTimeout = 0
        };
        PInvoke.FlashWindowEx(&info);
    }

    public double SetCornerRadius(Window window, double radius)
    {
        // The arbitrary radius is owned by the compositor patch. Disabling DWM's fixed Windows 11
        // radius also makes the fallback deterministic: an unavailable custom frame remains square.
        Win32Properties.SetWindowCornerPreference(
            window,
            Win32Properties.WindowCornerPreference.DoNotRound);

        // ReSharper disable once SuspiciousTypeConversion.Global
        // This is auto waved into Avalonia.Win32.WindowImpl by MonoMod in project `Everywhere.Patches.Avalonia.Win32`
        if (window.PlatformImpl is IWindowCornerRadiusFeature feature)
        {
            feature.SetCornerRadius(radius);
            window.InvalidateVisual();
            if (window is ChatWindow chatWindow)
            {
                ChatWindowShadow.Attach(chatWindow);
            }

            return radius;
        }

        return 0;
    }

    public void InitializeWindow(Window window)
    {
        if (window is ChatWindow chatWindow)
        {
            ChatWindowShadow.Attach(chatWindow);
        }
    }
}

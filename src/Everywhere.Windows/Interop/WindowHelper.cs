using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Controls;
using Everywhere.Interop;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Views;
using DrawingPoint = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Reference: Powertoys
/// </summary>
public sealed class WindowHelper : IWindowHelper
{
    private readonly ConditionalWeakTable<Window, WindowPropertiesState> _windowProperties = new();

    public unsafe void SetWindowProperties(
        Window window,
        bool? focusable = null,
        bool? hitTestVisible = null,
        WindowLayer? layer = null)
    {
        if (focusable is null && hitTestVisible is null && layer is null) return;

        if (!_windowProperties.TryGetValue(window, out var properties))
        {
            properties = new WindowPropertiesState(window, focusable, hitTestVisible, layer);
            _windowProperties.Add(window, properties);
        }
        else
        {
            properties.Update(focusable, hitTestVisible, layer);
        }

        if (focusable is { } isFocusable) window.Focusable = isFocusable;
        if (hitTestVisible is { } isHitTestVisible) window.IsHitTestVisible = isHitTestVisible;
        if (layer is { } windowLayer) window.Topmost = windowLayer >= WindowLayer.Topmost;

        if (window.TryGetPlatformHandle() is not { } handle) return;
        var hWnd = (HWND)handle.Handle;
        var style = (uint)PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var exStyle = (uint)PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var (updatedStyle, updatedExStyle) = properties.ApplyStyles(style, exStyle);

        if (updatedStyle != style)
        {
            PInvoke.SetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (int)updatedStyle);
        }

        if (updatedExStyle != exStyle)
        {
            PInvoke.SetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)updatedExStyle);
        }

        fixed (char* propertyName = "UIA_WindowVisibilityOverridden")
        {
            if (properties.IsHitTestVisible is true)
            {
                PInvoke.RemoveProp(hWnd, new PCWSTR(propertyName));
            }
            else if (properties.IsHitTestVisible is false)
            {
                PInvoke.SetProp(hWnd, new PCWSTR(propertyName), new HANDLE(2));
            }
        }

        if (properties.IsHitTestVisible is false)
        {
            PInvoke.SetLayeredWindowAttributes(hWnd, new COLORREF(), 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
        }

        PInvoke.SetWindowPos(
            hWnd,
            default,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
    }

    public void RaiseWindow(Window window)
    {
        if (window.TryGetPlatformHandle() is not { } handle) return;

        var layer = _windowProperties.TryGetValue(window, out var properties) && properties.Layer is { } configuredLayer ?
            configuredLayer :
            window.Topmost ?
                WindowLayer.Topmost :
                WindowLayer.Normal;
        var insertAfter = layer < WindowLayer.Topmost ? new HWND(0) : HWND.HWND_TOPMOST;
        PInvoke.SetWindowPos(
            (HWND)handle.Handle,
            insertAfter,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER);
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

    public bool? IsRegionCovered(Window window, PixelRect bounds)
    {
        if (window.TryGetPlatformHandle() is not { } handle || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var root = PInvoke.GetAncestor((HWND)handle.Handle, GET_ANCESTOR_FLAGS.GA_ROOT);
        if (root == 0) return null;

        var insetX = Math.Min(4, Math.Max(0, bounds.Width / 4));
        var insetY = Math.Min(4, Math.Max(0, bounds.Height / 4));
        Span<DrawingPoint> samplePoints =
        [
            new(bounds.Center.X, bounds.Center.Y),
            new(bounds.X + insetX, bounds.Y + insetY),
            new(bounds.Right - insetX - 1, bounds.Y + insetY),
            new(bounds.X + insetX, bounds.Bottom - insetY - 1),
            new(bounds.Right - insetX - 1, bounds.Bottom - insetY - 1)
        ];

        foreach (var samplePoint in samplePoints)
        {
            var hit = PInvoke.WindowFromPoint(samplePoint);
            if (hit == 0) return null;
            if (PInvoke.GetAncestor(hit, GET_ANCESTOR_FLAGS.GA_ROOT) != root) return false;
        }

        return true;
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
            if (PInvoke.GetForegroundWindow() != hWnd)
            {
                ActivateFromUiThread(hWnd);
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

    /// <summary>
    /// Uses the short-lived PowerToys foreground fallback. The UI thread shares
    /// input state with the current foreground thread only around one activation
    /// attempt and always detaches before returning.
    /// </summary>
    private static void ActivateFromUiThread(HWND hWnd)
    {
        var currentThreadId = PInvoke.GetCurrentThreadId();
        var foregroundWindow = PInvoke.GetForegroundWindow();
        var foregroundThreadId = foregroundWindow.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foregroundWindow, out _);
        var isAttached = foregroundThreadId != 0 &&
            foregroundThreadId != currentThreadId &&
            PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            PInvoke.BringWindowToTop(hWnd);
            PInvoke.SetForegroundWindow(hWnd);
            PInvoke.SetFocus(hWnd);
            PInvoke.SetActiveWindow(hWnd);
        }
        finally
        {
            if (isAttached)
            {
                PInvoke.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
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

    private sealed class WindowPropertiesState
    {
        public WindowLayer? Layer { get; private set; }

        public bool? IsHitTestVisible { get; private set; }

        private bool? IsFocusable { get; set; }

        public WindowPropertiesState(Window window, bool? focusable, bool? hitTestVisible, WindowLayer? layer)
        {
            Update(focusable, hitTestVisible, layer);
            Win32Properties.AddWindowStylesCallback(window, ApplyStyles);
            Win32Properties.AddWndProcHookCallback(window, WndProcHookCallback);
        }

        public void Update(bool? focusable, bool? hitTestVisible, WindowLayer? layer)
        {
            if (focusable.HasValue) IsFocusable = focusable;
            if (hitTestVisible.HasValue) IsHitTestVisible = hitTestVisible;
            if (layer.HasValue) Layer = layer;
        }

        public (uint style, uint exStyle) ApplyStyles(uint style, uint exStyle)
        {
            if (IsHitTestVisible is true)
            {
                style &= ~(uint)WINDOW_STYLE.WS_DISABLED;
                // Layering belongs to the renderer. Input-enabled transparent windows must retain it;
                // only the native mouse pass-through flag is controlled by hit-test visibility.
                exStyle &= ~(uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
            }
            else if (IsHitTestVisible is false)
            {
                style |= (uint)WINDOW_STYLE.WS_DISABLED;
                exStyle |= (uint)WINDOW_EX_STYLE.WS_EX_LAYERED | (uint)WINDOW_EX_STYLE.WS_EX_TRANSPARENT;
            }

            if (IsFocusable is true)
            {
                exStyle &= ~(uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            }
            else if (IsFocusable is false)
            {
                exStyle |= (uint)WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            }

            if (IsFocusable is true)
            {
                exStyle &= ~(uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
            }
            else if (IsFocusable is false)
            {
                exStyle |= (uint)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW;
            }

            return (style, exStyle);
        }

        private IntPtr WndProcHookCallback(IntPtr hWnd, uint msg, IntPtr wparam, IntPtr lparam, ref bool handled)
        {
            if (IsFocusable is not false) return IntPtr.Zero;

            if (msg == (uint)WINDOW_MESSAGE.WM_MOUSEACTIVATE)
            {
                handled = true;
                return 3; // MA_NOACTIVATE
            }

            // Activation and focus notifications must reach the default window procedure. In particular,
            // returning FALSE for WM_NCACTIVATE while deactivating prevents Windows from completing the
            // activation change and can consume the initiating mouse-down message.
            return IntPtr.Zero;
        }
    }
}
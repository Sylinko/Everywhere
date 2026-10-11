using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Windows.Interop;

internal sealed partial class WindowShadow
{
    private const uint DwmColorNone = 0xFFFFFFFE;

    private static readonly ConditionalWeakTable<Window, WindowShadow> Shadows = new();

    private readonly Window _owner;
    private readonly HWND _ownerHWnd;
    private readonly IWindowCompositionFeature _compositionFeature;
    private readonly Renderer _renderer;

    private bool? _cornerRadiusSuppressed;
    private bool? _borderSuppressed;
    private IDisposable? _cornerRadiusOverride;
    private IDisposable? _borderThicknessOverride;
    private bool _stateUpdateQueued;
    private bool _isActive;
    private bool _isDisposed;

    private WindowShadow(
        Window owner,
        HWND ownerHWnd,
        IWindowCompositionFeature compositionFeature,
        Renderer renderer)
    {
        _owner = owner;
        _ownerHWnd = ownerHWnd;
        _compositionFeature = compositionFeature;
        _renderer = renderer;
        _isActive = PInvoke.GetForegroundWindow() == ownerHWnd;
    }

    public static bool Attach(Window window, double radius)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (window.PlatformImpl is not IWindowCompositionFeature { IsCustomCornerRadiusSupported: true } compositionFeature)
        {
            return false;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            compositionFeature.SetCornerRadiusSuppressed(true);
            return false;
        }

        var hWnd = (HWND)handle.Handle;
        if (Shadows.TryGetValue(window, out var existingShadow))
        {
            if (existingShadow._ownerHWnd == hWnd)
            {
                compositionFeature.SetCornerRadius(radius);
                existingShadow.Update();
                compositionFeature.RequestCompositionFrame();
                return true;
            }

            existingShadow.Dispose();
            Shadows.Remove(window);
        }

        // Keep the radius suppressed if the companion HWND cannot be created. Its Composition
        // resources are initialized by the first content frame after renderer registration.
        compositionFeature.SetCornerRadiusSuppressed(true);
        var renderer = Renderer.TryCreate(hWnd);
        if (renderer is null) return false;

        compositionFeature.SetCornerRadius(radius);
        var shadow = new WindowShadow(window, hWnd, compositionFeature, renderer);
        shadow.Attach();
        Shadows.Add(window, shadow);
        return true;
    }

    private void Attach()
    {
        ConfigureDwmFrame();
        Win32Properties.AddWindowStylesCallback(_owner, WindowStylesCallback);
        Win32Properties.AddWndProcHookCallback(_owner, WndProcHookCallback);
        _compositionFeature.SetCustomRenderer(_renderer);
        ApplyNativeBehaviorStyles();
        Update();
        _compositionFeature.RequestCompositionFrame();
        QueueStateUpdate();
    }

    private unsafe void ConfigureDwmFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var borderColor = DwmColorNone;
        var result = PInvoke.DwmSetWindowAttribute(
            _ownerHWnd,
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &borderColor,
            sizeof(uint));
        if (result.Failed)
        {
            Log.ForContext<WindowShadow>().Warning(
                "Failed to disable the native window border: {ErrorCode}",
                result.Value);
        }
    }

    private void ApplyNativeBehaviorStyles()
    {
        var style = PInvoke.GetWindowLong(_ownerHWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var updatedStyle = style | (int)(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU);
        if (updatedStyle != style)
        {
            PInvoke.SetWindowLong(_ownerHWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, updatedStyle);
        }

        if (!PInvoke.SetWindowPos(
                _ownerHWnd,
                HWND.Null,
                0,
                0,
                0,
                0,
                SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE))
        {
            LogWin32Failure("apply the native window frame styles", _ownerHWnd);
        }
    }

    private static (uint style, uint exStyle) WindowStylesCallback(uint style, uint exStyle) =>
        (style | (uint)(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU), exStyle);

    private IntPtr WndProcHookCallback(
        IntPtr hWnd,
        uint msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        switch ((WINDOW_MESSAGE)msg)
        {
            case WINDOW_MESSAGE.WM_NCCALCSIZE:
                if (!IsWindowMaximized((HWND)hWnd))
                {
                    // Keep WS_CAPTION for native composition behavior without allowing its
                    // non-client metrics to shrink the restored or arranged client area.
                    handled = true;
                }

                break;
            case WINDOW_MESSAGE.WM_ACTIVATE:
                var isActive = (wParam.ToInt64() & 0xffff) != 0;
                if (_isActive != isActive)
                {
                    _isActive = isActive;
                    Update();
                    _compositionFeature.RequestCompositionFrame();
                }

                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_WINDOWPOSCHANGED:
                Update();
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_WINDOWPOSCHANGING:
                PositionBeforeOwnerChange(lParam);
                break;
            case WINDOW_MESSAGE.WM_EXITSIZEMOVE:
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_DESTROY:
                // Unregister while PlatformImpl is still available: Avalonia clears it
                // when its WM_DESTROY handler raises Closed, before WM_NCDESTROY.
                Dispose();
                break;
        }

        return IntPtr.Zero;
    }

    private unsafe void PositionBeforeOwnerChange(IntPtr lParam)
    {
        if (_isDisposed || lParam == IntPtr.Zero) return;
        var position = *(WINDOWPOS*)lParam;
        if ((position.flags & SET_WINDOW_POS_FLAGS.SWP_HIDEWINDOW) != 0)
        {
            _renderer.Hide();
            return;
        }

        // Restored/arranged windows have no non-client inset under our WM_NCCALCSIZE policy.
        // Do not predict transitional frame metrics: the final-state path handles those changes.
        if (!GetFramePresentation().ShowShadow ||
            (position.flags & SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED) != 0) return;
        var hasMove = (position.flags & SET_WINDOW_POS_FLAGS.SWP_NOMOVE) == 0;
        var hasSize = (position.flags & SET_WINDOW_POS_FLAGS.SWP_NOSIZE) == 0;
        if (!hasMove && !hasSize) return;
        if (!TryGetClientBounds(out var current)) return;
        var x = hasMove ? position.x : current.left;
        var y = hasMove ? position.y : current.top;
        var width = hasSize ? position.cx : current.Width;
        var height = hasSize ? position.cy : current.Height;
        if (width <= 0 || height <= 0) return;

        // Leave the owner's WINDOWPOS and default processing untouched. The changed notification
        // still corrects the prediction if Windows clamps the proposed size or changes window state.
        if (!_renderer.Position(new RECT(x, y, x + width, y + height), _owner.RenderScaling, _isActive))
        {
            _renderer.Hide();
        }
    }

    private static bool IsWindowMaximized(HWND window)
    {
        var placement = new WINDOWPLACEMENT();
        return PInvoke.GetWindowPlacement(window, ref placement) &&
            placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED;
    }

    private void Update()
    {
        var presentation = GetFramePresentation();
        ApplyFramePresentation(presentation);

        try
        {
            // Synchronize even when maximized/fullscreen hides the companion shadow HWND.
            // The native client rectangle is final here; never predict it from WINDOWPOS.
            _compositionFeature.SynchronizeCornerClip();
        }
        catch (Exception exception)
        {
            Log.ForContext<WindowShadow>().Warning(exception, "Failed to synchronize the native window corner clip");
        }

        if (!presentation.ShowShadow ||
            !TryGetClientBounds(out var frame) ||
            !_renderer.Position(
                frame,
                _owner.RenderScaling,
                _isActive))
        {
            _renderer.Hide();
        }
    }

    private WindowFramePresentation GetFramePresentation()
    {
        var isUnavailable = !PInvoke.IsWindowVisible(_ownerHWnd) || PInvoke.IsIconic(_ownerHWnd);
        var isFullScreen = _owner.WindowState == WindowState.FullScreen;
        var isMaximized = PInvoke.IsZoomed(_ownerHWnd) || _owner.WindowState == WindowState.Maximized;
        var isArranged = !isMaximized && PInvoke.IsWindowArranged(_ownerHWnd);

        return new WindowFramePresentation(
            SuppressCornerRadius: isUnavailable || isFullScreen || isMaximized || isArranged,
            SuppressBorder: isFullScreen || isMaximized,
            ShowShadow: !isUnavailable && !isFullScreen && !isMaximized);
    }

    private void ApplyFramePresentation(WindowFramePresentation presentation)
    {
        var shouldRequestFrame = false;
        if (_cornerRadiusSuppressed != presentation.SuppressCornerRadius)
        {
            _cornerRadiusSuppressed = presentation.SuppressCornerRadius;
            _compositionFeature.SetCornerRadiusSuppressed(presentation.SuppressCornerRadius);
            if (presentation.SuppressCornerRadius)
            {
                // Animation priority creates a temporary value above the AXAML local value.
                // Disposing it restores whichever corner radius is underneath.
                _cornerRadiusOverride = _owner.SetValue(
                    TemplatedControl.CornerRadiusProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
            }

            shouldRequestFrame = true;
        }

        if (_borderSuppressed != presentation.SuppressBorder)
        {
            _borderSuppressed = presentation.SuppressBorder;
            if (presentation.SuppressBorder)
            {
                _borderThicknessOverride = _owner.SetValue(
                    TemplatedControl.BorderThicknessProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
            }

            shouldRequestFrame = true;
        }

        if (shouldRequestFrame)
        {
            _compositionFeature.RequestCompositionFrame();
        }
    }

    private void QueueStateUpdate()
    {
        if (_stateUpdateQueued || _isDisposed)
        {
            return;
        }

        _stateUpdateQueued = true;
        Dispatcher.UIThread.Post(ProcessQueuedStateUpdate, DispatcherPriority.Background);
    }

    private void ProcessQueuedStateUpdate()
    {
        try
        {
            if (_isDisposed) return;

            // Win32 can finalize the arranged state after WM_WINDOWPOSCHANGED enters our hook.
            Update();
            if (!_renderer.SynchronizeTopmost(_ownerHWnd))
            {
                _renderer.Hide();
            }
        }
        finally
        {
            // Native corrections can synchronously send more owner messages. Keep them coalesced
            // until this update finishes instead of scheduling another correction from our own work.
            _stateUpdateQueued = false;
        }
    }

    private bool TryGetClientBounds(out RECT frame)
    {
        if (!PInvoke.GetClientRect(_ownerHWnd, out var clientRect))
        {
            var errorCode = Marshal.GetLastPInvokeError();
            if (PInvoke.IsWindow(_ownerHWnd) && PInvoke.IsWindowVisible(_ownerHWnd) && !PInvoke.IsIconic(_ownerHWnd))
            {
                LogWin32Failure("read the window client bounds", _ownerHWnd, errorCode);
            }

            frame = default;
            return false;
        }

        if (clientRect is not { Width: > 0, Height: > 0 })
        {
            frame = default;
            return false;
        }

        var origin = new Point(clientRect.left, clientRect.top);
        if (!PInvoke.ClientToScreen(_ownerHWnd, ref origin))
        {
            LogWin32Failure("map the window client bounds to the screen", _ownerHWnd);
            frame = default;
            return false;
        }

        frame = new RECT(origin.X, origin.Y, origin.X + clientRect.Width, origin.Y + clientRect.Height);
        return true;
    }

    private void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        // Wait for any in-flight frame and release its tree before destroying the shadow HWND.
        _compositionFeature.SetCustomRenderer(null);
        _compositionFeature.SetCornerRadiusSuppressed(false);

        if (Shadows.TryGetValue(_owner, out var currentShadow) && ReferenceEquals(currentShadow, this))
        {
            Shadows.Remove(_owner);
        }

        Win32Properties.RemoveWndProcHookCallback(_owner, WndProcHookCallback);
        Win32Properties.RemoveWindowStylesCallback(_owner, WindowStylesCallback);
        DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
        DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
        _renderer.Dispose();
    }

    private static void LogWin32Failure(string operation, HWND window, int? errorCode = null)
    {
        Log.ForContext<WindowShadow>().Error(
            "Failed to {Operation} for HWND {WindowHandle}: Win32 error {ErrorCode}",
            operation,
            window,
            errorCode ?? Marshal.GetLastPInvokeError());
    }

    private readonly record struct WindowFramePresentation(
        bool SuppressCornerRadius,
        bool SuppressBorder,
        bool ShowShadow
    );
}
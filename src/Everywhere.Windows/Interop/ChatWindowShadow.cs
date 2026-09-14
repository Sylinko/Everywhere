using System.Drawing;
using System.Runtime.CompilerServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using SkiaSharp;

namespace Everywhere.Windows.Interop;

internal sealed class ChatWindowShadow
{
    private const uint DwmColorNone = 0xFFFFFFFE;

    private static readonly ConditionalWeakTable<ChatWindow, ChatWindowShadow> Shadows = new();

    private readonly ChatWindow _window;
    private readonly HWND _owner;
    private readonly IWindowCornerRadiusFeature _cornerRadiusFeature;
    private readonly Renderer _renderer;

    private bool? _cornerRadiusSuppressed;
    private bool? _borderSuppressed;
    private IDisposable? _cornerRadiusOverride;
    private IDisposable? _borderThicknessOverride;
    private bool _stateUpdateQueued;
    private bool _isActive;
    private bool _isDisposed;

    private ChatWindowShadow(
        ChatWindow window,
        HWND owner,
        IWindowCornerRadiusFeature cornerRadiusFeature,
        Renderer renderer)
    {
        _window = window;
        _owner = owner;
        _cornerRadiusFeature = cornerRadiusFeature;
        _renderer = renderer;
        _isActive = PInvoke.GetForegroundWindow() == owner;
    }

    public static void Attach(ChatWindow window)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (window.PlatformImpl is not IWindowCornerRadiusFeature cornerRadiusFeature)
        {
            return;
        }

        if (window.TryGetPlatformHandle() is not { } handle)
        {
            cornerRadiusFeature.SetCornerRadiusSuppressed(true);
            return;
        }

        var hWnd = (HWND)handle.Handle;
        if (Shadows.TryGetValue(window, out var existingShadow))
        {
            if (existingShadow._owner == hWnd)
            {
                existingShadow.Update();
                return;
            }

            existingShadow.Dispose();
            Shadows.Remove(window);
        }

        // Fail closed until the shadow renderer is available. A later SetCornerRadius call can
        // still store the requested radius without exposing a clipped content-only window.
        cornerRadiusFeature.SetCornerRadiusSuppressed(true);
        var renderer = Renderer.TryCreate(hWnd);
        if (renderer is null) return;

        var shadow = new ChatWindowShadow(window, hWnd, cornerRadiusFeature, renderer);
        shadow.Attach();
        Shadows.Add(window, shadow);
    }

    private void Attach()
    {
        ConfigureDwmFrame();
        Win32Properties.AddWindowStylesCallback(_window, WindowStylesCallback);
        Win32Properties.AddWndProcHookCallback(_window, WndProcHookCallback);
        ApplyNativeBehaviorStyles();
        Update();
    }

    private unsafe void ConfigureDwmFrame()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var borderColor = DwmColorNone;
        PInvoke.DwmSetWindowAttribute(
            _owner,
            DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR,
            &borderColor,
            sizeof(uint));
    }

    private void ApplyNativeBehaviorStyles()
    {
        var style = PInvoke.GetWindowLong(_owner, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var updatedStyle = style | (int)(WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_SYSMENU);
        if (updatedStyle != style)
        {
            PInvoke.SetWindowLong(_owner, WINDOW_LONG_PTR_INDEX.GWL_STYLE, updatedStyle);
        }

        PInvoke.SetWindowPos(
            _owner,
            HWND.Null,
            0,
            0,
            0,
            0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED |
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER |
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
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
                _isActive = (wParam.ToInt64() & 0xffff) != 0;
                Update();
                break;
            case WINDOW_MESSAGE.WM_WINDOWPOSCHANGED:
                Update();
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_EXITSIZEMOVE:
                QueueStateUpdate();
                break;
            case WINDOW_MESSAGE.WM_NCDESTROY:
                Dispose();
                break;
        }

        return IntPtr.Zero;
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

        if (!presentation.ShowShadow ||
            !_cornerRadiusFeature.TryGetEffectiveCornerRadius(out var logicalRadius) ||
            !TryGetClientBounds(out var frame) ||
            !_renderer.Update(
                frame,
                _window.RenderScaling,
                logicalRadius,
                _isActive))
        {
            _renderer.Hide();
        }
    }

    private WindowFramePresentation GetFramePresentation()
    {
        var isUnavailable = !PInvoke.IsWindowVisible(_owner) || PInvoke.IsIconic(_owner);
        var isFullScreen = _window.WindowState == WindowState.FullScreen;
        var isMaximized = PInvoke.IsZoomed(_owner) || _window.WindowState == WindowState.Maximized;
        var isArranged = !isMaximized && PInvoke.IsWindowArranged(_owner);

        return new WindowFramePresentation(
            SuppressCornerRadius: isUnavailable || isFullScreen || isMaximized || isArranged,
            SuppressBorder: isFullScreen || isMaximized,
            ShowShadow: !isUnavailable && !isFullScreen && !isMaximized);
    }

    private void ApplyFramePresentation(WindowFramePresentation presentation)
    {
        var invalidateVisual = false;
        if (_cornerRadiusSuppressed != presentation.SuppressCornerRadius)
        {
            _cornerRadiusSuppressed = presentation.SuppressCornerRadius;
            _cornerRadiusFeature.SetCornerRadiusSuppressed(presentation.SuppressCornerRadius);
            if (presentation.SuppressCornerRadius)
            {
                // Animation priority creates a temporary value above the AXAML local value.
                // Disposing it restores whichever corner radius is underneath.
                _cornerRadiusOverride = _window.SetValue(
                    TemplatedControl.CornerRadiusProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
            }

            invalidateVisual = true;
        }

        if (_borderSuppressed != presentation.SuppressBorder)
        {
            _borderSuppressed = presentation.SuppressBorder;
            if (presentation.SuppressBorder)
            {
                _borderThicknessOverride = _window.SetValue(
                    TemplatedControl.BorderThicknessProperty,
                    default,
                    BindingPriority.Animation);
            }
            else
            {
                DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
            }

            invalidateVisual = true;
        }

        if (invalidateVisual)
        {
            _window.InvalidateVisual();
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
        _stateUpdateQueued = false;
        if (!_isDisposed)
        {
            // Win32 can finalize the arranged state after WM_WINDOWPOSCHANGED enters our hook.
            Update();
        }
    }

    private bool TryGetClientBounds(out RECT frame)
    {
        if (!PInvoke.GetClientRect(_owner, out var clientRect) || clientRect is not { Width: > 0, Height: > 0 })
        {
            frame = default;
            return false;
        }

        var origin = new Point(clientRect.left, clientRect.top);
        if (!PInvoke.ClientToScreen(_owner, ref origin))
        {
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

        _cornerRadiusFeature.SetCornerRadiusSuppressed(false);

        if (Shadows.TryGetValue(_window, out var currentShadow) && ReferenceEquals(currentShadow, this))
        {
            Shadows.Remove(_window);
        }

        Win32Properties.RemoveWndProcHookCallback(_window, WndProcHookCallback);
        Win32Properties.RemoveWindowStylesCallback(_window, WindowStylesCallback);
        DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
        DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
        _renderer.Dispose();
    }

    private readonly record struct WindowFramePresentation(
        bool SuppressCornerRadius,
        bool SuppressBorder,
        bool ShowShadow
    );


    private sealed class Renderer : IDisposable
    {
        private const float ShadowPadding = 24;
        private const float ShadowBlurRadius = 24;
        private const byte ActiveShadowAlpha = 200;
        private const byte InactiveShadowAlpha = 100;

        private HWND _window;
        private bool _isVisible;
        private int _frameWidth;
        private int _frameHeight;
        private double _scaling;
        private float _radius = float.NaN;
        private byte _alpha;

        private Renderer(HWND window)
        {
            _window = window;
        }

        public static unsafe Renderer? TryCreate(HWND owner)
        {
            using var hInstance = PInvoke.GetModuleHandle();
            var window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_LAYERED |
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                "STATIC",
                "Everywhere.ChatWindowShadow",
                WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_DISABLED,
                0,
                0,
                1,
                1,
                hWndParent: owner,
                hInstance: hInstance);

            if (window.IsNull)
            {
                return null;
            }

            fixed (char* propertyName = "UIA_WindowVisibilityOverridden")
            {
                // Keep the auxiliary HWND visible to desktop composition while hiding it from UIA-
                // based window discovery, so capture tools can resolve the owner as the real window.
                PInvoke.SetProp(window, new PCWSTR(propertyName), new HANDLE(2));
            }

            return new Renderer(window);
        }

        public bool Update(RECT frame, double scaling, double cornerRadius, bool isActive)
        {
            if (_window.IsNull)
            {
                return false;
            }

            var radius = (float)(cornerRadius * scaling);
            radius = Math.Min(radius, Math.Min(frame.Width, frame.Height) / 2f);
            var padding = (int)Math.Ceiling(ShadowPadding * scaling);
            var width = frame.Width + padding * 2;
            var height = frame.Height + padding * 2;
            var alpha = isActive ? ActiveShadowAlpha : InactiveShadowAlpha;
            var redraw =
                _frameWidth != frame.Width ||
                _frameHeight != frame.Height ||
                Math.Abs(_scaling - scaling) > double.Epsilon ||
                float.IsNaN(_radius) ||
                Math.Abs(_radius - radius) > float.Epsilon ||
                _alpha != alpha;

            if (redraw)
            {
                if (!Render(
                        frame.X - padding,
                        frame.Y - padding,
                        width,
                        height,
                        padding,
                        radius,
                        scaling,
                        alpha))
                {
                    return false;
                }

                _frameWidth = frame.Width;
                _frameHeight = frame.Height;
                _scaling = scaling;
                _radius = radius;
                _alpha = alpha;
            }
            else if (!PInvoke.SetWindowPos(
                         _window,
                         HWND.Null,
                         frame.X - padding,
                         frame.Y - padding,
                         width,
                         height,
                         SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER))
            {
                return false;
            }

            if (!_isVisible)
            {
                PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
                _isVisible = true;
            }

            return true;
        }

        public void Hide()
        {
            if (!_isVisible || _window.IsNull)
            {
                return;
            }

            PInvoke.ShowWindow(_window, SHOW_WINDOW_CMD.SW_HIDE);
            _isVisible = false;
        }

        public void Dispose()
        {
            if (_window.IsNull)
            {
                return;
            }

            PInvoke.DestroyWindow(_window);
            _window = HWND.Null;
            _isVisible = false;
        }

        private unsafe bool Render(
            int x,
            int y,
            int width,
            int height,
            int padding,
            float radius,
            double scaling,
            byte alpha)
        {
            var screenDc = PInvoke.GetDC(HWND.Null);
            if (screenDc.IsNull)
            {
                return false;
            }

            var memoryDc = PInvoke.CreateCompatibleDC(screenDc);
            if (memoryDc.IsNull)
            {
                PInvoke.ReleaseDC(HWND.Null, screenDc);
                return false;
            }

            HBITMAP bitmap = default;
            HGDIOBJ previousBitmap = default;
            try
            {
                var bitmapInfo = new BITMAPINFO
                {
                    bmiHeader = new BITMAPINFOHEADER
                    {
                        biSize = (uint)sizeof(BITMAPINFOHEADER),
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = (uint)BI_COMPRESSION.BI_RGB
                    }
                };
                void* pixels = null;
                bitmap = PInvoke.CreateDIBSection(
                    memoryDc,
                    &bitmapInfo,
                    DIB_USAGE.DIB_RGB_COLORS,
                    &pixels,
                    default,
                    0);

                if (bitmap.IsNull || pixels is null)
                {
                    return false;
                }

                previousBitmap = PInvoke.SelectObject(memoryDc, bitmap);
                using var surface = SKSurface.Create(
                    new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul),
                    (IntPtr)pixels,
                    width * 4);
                if (surface is null)
                {
                    return false;
                }

                var canvas = surface.Canvas;
                canvas.Clear(SKColors.Transparent);
                var frame = new SKRect(padding, padding, width - padding, height - padding);
                using var roundRect = new SKRoundRect(frame, radius, radius);
                canvas.Save();
                canvas.ClipRoundRect(roundRect, SKClipOperation.Difference, antialias: true);

                var sigma = BlurRadiusToSigma(ShadowBlurRadius) * (float)scaling;
                using var shadowFilter = SKImageFilter.CreateBlur(sigma, sigma);
                using var shadowPaint = new SKPaint();
                shadowPaint.IsAntialias = true;
                shadowPaint.Color = new SKColor(0, 0, 0, alpha);
                shadowPaint.ImageFilter = shadowFilter;
                canvas.DrawRoundRect(roundRect, shadowPaint);
                canvas.Restore();
                canvas.Flush();

                var destination = new Point(x, y);
                var size = new SIZE(width, height);
                var source = new Point(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = 0,
                    SourceConstantAlpha = byte.MaxValue,
                    AlphaFormat = 1
                };

                return PInvoke.UpdateLayeredWindow(
                    _window,
                    screenDc,
                    &destination,
                    &size,
                    memoryDc,
                    &source,
                    default,
                    &blend,
                    UPDATE_LAYERED_WINDOW_FLAGS.ULW_ALPHA);
            }
            finally
            {
                if (!previousBitmap.IsNull)
                {
                    PInvoke.SelectObject(memoryDc, previousBitmap);
                }

                if (!bitmap.IsNull)
                {
                    PInvoke.DeleteObject(bitmap);
                }

                PInvoke.DeleteDC(memoryDc);
                PInvoke.ReleaseDC(HWND.Null, screenDc);
            }
        }

        private static float BlurRadiusToSigma(float radius) =>
            radius <= 0 ? 0 : 0.288675f * radius + 0.5f;
    }
}
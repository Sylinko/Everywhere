using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Rendering;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using ZLinq;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    /// <summary>
    /// Represents a modal screen selection session (e.g., picking a window or element).
    /// <para>
    /// The session window is a normal opaque-to-input Avalonia window that sits topmost.
    /// Mouse and keyboard events are handled through standard Avalonia overrides.
    /// MaskWindows are kept mouse-transparent (hit-test invisible) so they do not interfere
    /// with the standard event routing to this window.
    /// </para>
    /// </summary>
    private abstract class ScreenSelectionSessionWindow : ScreenSelectionSessionWindowBase
    {
        protected IWindowHelper WindowHelper { get; }
        protected ScreenSelectionMaskWindow[] MaskWindows { get; }
        protected ScreenSelectionToolTipWindow ToolTipWindow { get; }
        protected IVisualElement? PickingElement { get; private set; }

        private readonly HashSet<HWND> _ownWindows = [];
        private IDisposable? _keyboardHookSubscription;

        protected ScreenSelectionSessionWindow(
            IWindowHelper windowHelper,
            ScreenSelectionModes allowedModes,
            ScreenSelectionModes initialMode) : base(allowedModes, initialMode)
        {
            WindowHelper = windowHelper;

            var allScreens = Screens.All;
            MaskWindows = new ScreenSelectionMaskWindow[allScreens.Count];
            var allScreenBounds = new PixelRect();
            for (var i = 0; i < allScreens.Count; i++)
            {
                var screen = allScreens[i];
                allScreenBounds = allScreenBounds.Union(screen.Bounds);
                var maskWindow = new ScreenSelectionMaskWindow(screen.Bounds);
                windowHelper.SetHitTestVisible(maskWindow, false);
                MaskWindows[i] = maskWindow;
            }

            // Cover the entire virtual screen
            SetPlacement(allScreenBounds, out _);

            ToolTipWindow = new ScreenSelectionToolTipWindow(allowedModes, initialMode);
            windowHelper.SetHitTestVisible(ToolTipWindow, false);
        }

        protected override unsafe void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (TryGetPlatformHandle()?.Handle is { } hWnd and > 0)
            {
                var exStyle = PInvoke.GetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                PInvoke.SetWindowLong((HWND)hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, exStyle | (int)WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
                PInvoke.SetLayeredWindowAttributes((HWND)hWnd, new COLORREF(0), 254, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

                fixed (char* pStr = "UIA_WindowVisibilityOverridden")
                {
                    PInvoke.SetProp((HWND)hWnd, new PCWSTR(pStr), new HANDLE(2));
                }
            }

            // Collect all overlay HWNDs so PickElement can skip them when looking for the window behind.
            foreach (var maskWindow in MaskWindows.AsValueEnumerable()) maskWindow.Show();

            _ownWindows.Clear();
            foreach (var w in MaskWindows.AsValueEnumerable().Cast<Window>().Append(this).Append(ToolTipWindow))
            {
                if (w.TryGetPlatformHandle()?.Handle is { } h and not 0)
                    _ownWindows.Add((HWND)h);
            }

            // Install a low-level keyboard hook as a safety net for focus-loss scenarios
            // (e.g., Alt+Tab). The keyboard hook ensures Escape always cancels the session
            // even if this window loses focus.
            _keyboardHookSubscription ??= LowLevelHook.CreateKeyboardHook(HandleKeyboardHook, false);

            // Pick the element under the cursor immediately
            Dispatcher.UIThread.Post(UpdatePickingElement);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            var pos = e.GetPosition(null);
            var screenPos = ((IRenderRoot)this).PointToScreen(pos);
            var cursorPos = new Point(screenPos.X, screenPos.Y);
            SetToolTipWindowPosition(cursorPos);
            UpdatePickingElement(cursorPos);
        }

        private void HandleKeyboardHook(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            // Only intercept key-down events from the hook (focus-loss safety net)
            var isKeyDown = msg is WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN;
            if (!isKeyDown) return;

            // Only act on Escape via the hook; other keys are handled by OnKeyDown when the window has focus.
            if ((VIRTUAL_KEY)hookStruct.vkCode == VIRTUAL_KEY.VK_ESCAPE)
            {
                blockNext = true;
                Dispatcher.UIThread.Post(Cancel);
            }
        }

        protected override void HandleModeChanged()
        {
            ToolTipWindow.ToolTip.CurrentMode = CurrentMode;
            UpdatePickingElement();
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            DisposeCollector.DisposeToDefault(ref _keyboardHookSubscription);
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            foreach (var maskWindow in MaskWindows) maskWindow.Close();
            ToolTipWindow.Close();

            base.OnClosed(e);
        }

        private void UpdatePickingElement()
        {
            if (!PInvoke.GetCursorPos(out var cursorPos)) return;

            UpdatePickingElement(cursorPos);
            SetToolTipWindowPosition(cursorPos);
        }

        /// <summary>
        /// Finds the topmost window at <paramref name="screenPoint"/> that does not belong to this session's
        /// own overlay windows (mask windows, tooltip, or the session window itself).
        /// </summary>
        private unsafe HWND FindWindowBehindOwnOverlays(Point screenPoint)
        {
            var result = HWND.Null;
            PInvoke.EnumWindows(
                (hWnd, _) =>
                {
                    if (_ownWindows.Contains(hWnd)) return true;
                    if (!PInvoke.IsWindowVisible(hWnd)) return true;
                    if (PInvoke.IsIconic(hWnd)) return true;
                    if (PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) is var exStyle &&
                        (exStyle & (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0)
                        return true; // skip non-activatable windows (e.g., tool windows, some system windows)

                    // Skip cloaked windows (e.g., UWP apps on other virtual desktops)
                    long cloaked = 0;
                    PInvoke.DwmGetWindowAttribute(hWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(long));
                    if (cloaked != 0) return true;

                    PInvoke.GetWindowRect(hWnd, out var rect);
                    if (rect.left <= screenPoint.X && screenPoint.X < rect.right && rect.top <= screenPoint.Y && screenPoint.Y < rect.bottom)
                    {
                        result = hWnd;
                        return false; // stop enumeration
                    }

                    return true;
                },
                0);

            return result;
        }

        protected override void OnCanceled()
        {
            PickingElement = null;
        }

        /// <summary>
        /// Picks the element under the cursor based on the current selection mode.
        /// </summary>
        /// <param name="cursorPos"></param>
        protected virtual void UpdatePickingElement(Point cursorPos)
        {
            var maskRect = default(PixelRect);
            switch (CurrentMode)
            {
                case ScreenSelectionModes.Screen:
                {
                    var pixelPoint = new PixelPoint(cursorPos.X, cursorPos.Y);
                    var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pixelPoint));
                    if (screen == null) break;

                    var hMonitor = PInvoke.MonitorFromPoint(cursorPos, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                    if (hMonitor == HMONITOR.Null) break;

                    PickingElement = new ScreenVisualElementImpl(hMonitor);
                    maskRect = screen.Bounds;
                    break;
                }
                case ScreenSelectionModes.Window:
                {
                    var targetHWnd = FindWindowBehindOwnOverlays(cursorPos);
                    if (targetHWnd.IsNull) break;

                    var rootHWnd = PInvoke.GetAncestor(targetHWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
                    if (rootHWnd.IsNull) break;

                    PickingElement = TryCreateVisualElement(() => Automation.FromHandle(rootHWnd));
                    if (PickingElement == null) break;

                    maskRect = PickingElement.BoundingRectangle;
                    break;
                }
                case ScreenSelectionModes.Element:
                {
                    PickingElement = TryCreateVisualElement(() => Automation.FromPoint(new Point(cursorPos.X, cursorPos.Y)));

                    if (PickingElement == null) break;

                    maskRect = PickingElement.BoundingRectangle;
                    break;
                }
            }

            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
            ToolTipWindow.ToolTip.Element = PickingElement;
        }

        private void SetToolTipWindowPosition(Point cursorPos)
        {
            const int margin = 16;

            var pointerPoint = new PixelPoint(cursorPos.X, cursorPos.Y);
            var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pointerPoint));
            if (screen == null) return;

            Size tooltipSize;
            if (!ToolTipWindow.IsVisible)
            {
                ToolTipWindow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                tooltipSize = ToolTipWindow.DesiredSize * screen.Scaling;
            }
            else
            {
                tooltipSize = ToolTipWindow.Bounds.Size * screen.Scaling;
            }

            var x = (double)pointerPoint.X;
            var y = pointerPoint.Y - margin - tooltipSize.Height;

            // Check if there is enough space above the pointer
            if (y < 0d)
            {
                y = pointerPoint.Y + margin; // place below the pointer
            }

            // Check if there is enough space to the right of the pointer
            if (x + tooltipSize.Width > screen.Bounds.Right)
            {
                x = pointerPoint.X - tooltipSize.Width; // place to the left of the pointer
            }

            ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
            ToolTipWindow.IsVisible = true; // Show after set position
        }
    }
}
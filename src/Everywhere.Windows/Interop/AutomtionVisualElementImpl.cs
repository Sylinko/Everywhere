using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Extensions;
using Everywhere.Interop;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using Serilog;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    private class AutomationVisualElementImpl(AutomationElement element) : IVisualElement
    {
        private static readonly TextAttributeId IsSelectionActivePropertyId =
            TextAttributeId.Register(AutomationType.UIA3, 30034, "IsSelectionActive");

        public string Id { get; } = string.Join('.', element.Properties.RuntimeId.ValueOrDefault?.Select(x => x.ToString("X")) ?? []);

        public IVisualElement? Parent
        {
            get
            {
                try
                {
                    if (IsTopLevelWindow)
                    {
                        // this is a top level window
                        var screen = PInvoke.MonitorFromWindow((HWND)NativeWindowHandle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
                        return screen == HMONITOR.Null ? null : new ScreenVisualElementImpl(screen);
                    }

                    var parent = TreeWalker.GetParent(_element);
                    return parent is null ? null : new AutomationVisualElementImpl(parent);
                }
                catch
                {
                    return null;
                }
            }
        }

        public IEnumerable<IVisualElement> Children
        {
            get
            {
                AutomationElement? child;
                try
                {
                    child = TreeWalker.GetFirstChild(_element);
                }
                catch
                {
                    yield break;
                }

                while (child is not null)
                {
                    AutomationVisualElementImpl item;
                    try
                    {
                        item = new AutomationVisualElementImpl(child);
                    }
                    catch
                    {
                        yield break;
                    }

                    yield return item;

                    try
                    {
                        child = TreeWalker.GetNextSibling(child);
                    }
                    catch
                    {
                        yield break;
                    }
                }
            }
        }

        public VisualElementSiblingAccessor SiblingAccessor => new SiblingAccessorImpl(this);

        public VisualElementType Type
        {
            get
            {
                try
                {
                    return _element.Properties.ControlType.ValueOrDefault switch
                    {
                        ControlType.AppBar => VisualElementType.Menu,
                        ControlType.Button => VisualElementType.Button,
                        ControlType.Calendar => VisualElementType.Label,
                        ControlType.CheckBox => VisualElementType.CheckBox,
                        ControlType.ComboBox => VisualElementType.ComboBox,
                        ControlType.DataGrid => VisualElementType.DataGrid,
                        ControlType.DataItem => VisualElementType.DataGridItem,
                        ControlType.Document => VisualElementType.Document,
                        ControlType.Edit => VisualElementType.TextEdit,
                        ControlType.Group => VisualElementType.Panel,
                        ControlType.Header or ControlType.HeaderItem => VisualElementType.TableRow,
                        ControlType.Hyperlink => VisualElementType.Hyperlink,
                        ControlType.Image => VisualElementType.Image,
                        ControlType.List => VisualElementType.ListView,
                        ControlType.ListItem => VisualElementType.ListViewItem,
                        ControlType.Menu or ControlType.MenuBar => VisualElementType.Menu,
                        ControlType.MenuItem => VisualElementType.MenuItem,
                        ControlType.Pane when IsTopLevelWindow => VisualElementType.TopLevel,
                        ControlType.Pane => VisualElementType.Panel, // a child window, treat as panel
                        ControlType.ProgressBar => VisualElementType.ProgressBar,
                        ControlType.RadioButton => VisualElementType.RadioButton,
                        ControlType.ScrollBar => VisualElementType.ScrollBar,
                        ControlType.SemanticZoom => VisualElementType.ListView,
                        ControlType.Separator => VisualElementType.Unknown,
                        ControlType.Slider or ControlType.Spinner => VisualElementType.Slider,
                        ControlType.SplitButton => VisualElementType.Button,
                        ControlType.StatusBar => VisualElementType.Panel,
                        ControlType.Tab => VisualElementType.TabControl,
                        ControlType.TabItem => VisualElementType.TabItem,
                        ControlType.Table => VisualElementType.Table,
                        ControlType.Text => VisualElementType.Label,
                        ControlType.Thumb => VisualElementType.Slider,
                        ControlType.TitleBar or ControlType.ToolBar or ControlType.ToolTip => VisualElementType.Panel,
                        ControlType.Tree => VisualElementType.TreeView,
                        ControlType.TreeItem => VisualElementType.TreeViewItem,
                        ControlType.Window when IsTopLevelWindow => VisualElementType.TopLevel,
                        ControlType.Window => VisualElementType.Panel, // a child window, treat as panel
                        _ => VisualElementType.Unknown
                    };
                }
                catch
                {
                    return VisualElementType.Unknown;
                }
            }
        }

        public VisualElementStates States
        {
            get
            {
                try
                {
                    var states = VisualElementStates.None;
                    if (_element.Properties.IsOffscreen.ValueOrDefault)
                        states |= VisualElementStates.Offscreen;
                    if (!_element.Properties.IsEnabled.ValueOrDefault)
                        states |= VisualElementStates.Disabled;
                    if (_element.Properties.HasKeyboardFocus.ValueOrDefault)
                        states |= VisualElementStates.Focused;
                    if (_element.TryGetSelectionItemPattern() is { IsSelected.ValueOrDefault: true })
                        states |= VisualElementStates.Selected;
                    if (_element.TryGetValuePattern() is { IsReadOnly.ValueOrDefault: true })
                        states |= VisualElementStates.ReadOnly;
                    if (_element.Properties.IsPassword.ValueOrDefault)
                        states |= VisualElementStates.Password;
                    return states;
                }
                catch
                {
                    return VisualElementStates.None;
                }
            }
        }

        public string? Name
        {
            get
            {
                try
                {
                    if (_element.Properties.Name.TryGetValue(out var name)) return name;
                    if (_element.TryGetLegacyIAccessiblePattern() is { } accessiblePattern) return accessiblePattern.Name;
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public PixelRect BoundingRectangle => GetBoundingRectangle(_element);

        public int ProcessId { get; } = element.FrameworkAutomationElement.ProcessId.ValueOrDefault;

        public nint NativeWindowHandle { get; } = element.FrameworkAutomationElement.NativeWindowHandle.ValueOrDefault;

        private readonly AutomationElement _element = element;

        public string? GetText(int maxLength = -1)
        {
            try
            {
                if (_element.TryGetValuePattern() is { } valuePattern) return valuePattern.Value;
                if (_element.TryGetTextPattern() is { } textPattern) return textPattern.DocumentRange.GetText(maxLength);
                if (_element.TryGetLegacyIAccessiblePattern() is { } accessiblePattern) return accessiblePattern.Value;
                return null;
            }
            catch
            {
                return null;
            }
        }

        public void Invoke()
        {
            try
            {
                if (_element.TryGetInvokePattern() is { } invokePattern)
                {
                    invokePattern.Invoke();
                    return;
                }
            }
            catch (Exception ex)
            {
                // ignore
                LogError(ex, "InvokePattern");
            }

            try
            {
                if (_element.TryGetTogglePattern() is { } togglePattern)
                {
                    togglePattern.Toggle();
                    return;
                }
            }
            catch (Exception ex)
            {
                // ignore
                LogError(ex, "TogglePattern");
            }

            try
            {
                if (_element.TryGetSelectionItemPattern() is { } selectionItemPattern)
                {
                    selectionItemPattern.Select();
                    return;
                }
            }
            catch (Exception ex)
            {
                // ignore
                LogError(ex, "SelectionItemPattern");
            }

            try
            {
                if (_element.TryGetExpandCollapsePattern() is { } expandCollapsePattern)
                {
                    var state = expandCollapsePattern.ExpandCollapseState.ValueOrDefault;
                    if (state is ExpandCollapseState.Collapsed or ExpandCollapseState.PartiallyExpanded)
                    {
                        expandCollapsePattern.Expand();
                    }
                    else
                    {
                        expandCollapsePattern.Collapse();
                    }

                    return;
                }
            }
            catch (Exception ex)
            {
                // ignore
                LogError(ex, "ExpandCollapsePattern");
            }

            try
            {
                if (_element.TryGetLegacyIAccessiblePattern() is { } legacyPattern)
                {
                    legacyPattern.DoDefaultAction();
                }
            }
            catch (Exception ex)
            {
                // ignore
                LogError(ex, "LegacyIAccessiblePattern");
            }

            // Last try, get clickable point and Send mouse click
            if (!_element.TryGetClickablePoint(out var point))
            {
                throw new InvalidOperationException("The target element does not support invocation.");
            }

            // Ensure the point is within screen bounds
            var screenLeft = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
            var screenTop = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
            var screenWidth = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
            var screenHeight = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
            if (point.X < screenLeft || point.X >= screenLeft + screenWidth ||
                point.Y < screenTop || point.Y >= screenTop + screenHeight)
            {
                throw new InvalidOperationException("The clickable point of the target element is outside of the screen bounds.");
            }

            if (TryGetAncestorWithNativeWindowHandle(_element, out var hWnd) is not { } windowElement)
            {
                throw new InvalidOperationException("The target element does not belong to a valid window.");
            }

            var rootHwnd = PInvoke.GetAncestor((HWND)hWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
            if (rootHwnd != 0) PInvoke.SetForegroundWindow(rootHwnd);

            windowElement.FocusNative();

            // Ensure window is foreground
            var windowFromPoint = PInvoke.WindowFromPoint(point);
            if (windowFromPoint == 0 || PInvoke.GetAncestor(windowFromPoint, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != rootHwnd)
            {
                throw new InvalidOperationException("Failed to bring the target element's window to the foreground.");
            }

            // Send mouse click to the point
            PInvoke.SendInput(
                [
                    new INPUT
                    {
                        Anonymous =
                        {
                            mi =
                            {
                                dx = point.X * 65535 / PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN),
                                dy = point.Y * 65535 / PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN),
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_ABSOLUTE | MOUSE_EVENT_FLAGS.MOUSEEVENTF_MOVE,
                            }
                        },
                        type = INPUT_TYPE.INPUT_MOUSE,
                    },
                    new INPUT
                    {
                        Anonymous =
                        {
                            mi =
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTDOWN,
                            }
                        },
                        type = INPUT_TYPE.INPUT_MOUSE,
                    }
                ],
                Unsafe.SizeOf<INPUT>());

            // A short delay to ensure the click is done before sending mouse up
            Thread.Sleep(30);
            PInvoke.SendInput(
                [
                    new INPUT
                    {
                        Anonymous =
                        {
                            mi =
                            {
                                dwFlags = MOUSE_EVENT_FLAGS.MOUSEEVENTF_LEFTUP,
                            }
                        },
                        type = INPUT_TYPE.INPUT_MOUSE,
                    }
                ],
                Unsafe.SizeOf<INPUT>());

            void LogError(Exception ex, string action) =>
                Log.ForContext<AutomationVisualElementImpl>().Information(ex, "Failed to perform {Action} on element {Type}", action, Type);
        }

        public void SetText(string text)
        {
            try
            {
                if (_element.TryGetValuePattern() is { } valuePattern)
                {
                    if (valuePattern.IsReadOnly.ValueOrDefault)
                    {
                        throw new InvalidOperationException("The target element is read-only and cannot accept text.");
                    }

                    _element.Focus();
                    new TextBox(_element.FrameworkAutomationElement).Text = text;
                    return;
                }
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException("Failed to set text on the element through UI Automation.", ex);
            }
            catch (Exception ex) when (IsAutomationException(ex))
            {
                throw new InvalidOperationException("Failed to set text on the element through UI Automation.", ex);
            }

            throw new NotSupportedException("The target element does not support programmatic text input.");
        }

        public void SendShortcut(KeyboardShortcut shortcut)
        {
            if (TryGetAncestorWithNativeWindowHandle(_element, out var hWnd) is not { } windowElement)
            {
                throw new InvalidOperationException("The target element does not belong to a valid window.");
            }

            var rootHwnd = PInvoke.GetAncestor((HWND)hWnd, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
            if (rootHwnd != 0) PInvoke.SetForegroundWindow(rootHwnd);

            windowElement.FocusNative();
            SendInput(shortcut);
        }

        public string? GetSelectionText()
        {
            try
            {
                // 1) Prefer UIA TextPattern selection text
                if (_element.TryGetTextPattern() is { } textPattern)
                {
                    try
                    {
                        var ranges = textPattern.GetSelection();
                        if (ranges is { Length: > 0 })
                        {
                            var selected = string.Join(null, ranges.Select(r => r.GetText(-1)));
                            if (!string.IsNullOrEmpty(selected))
                                return selected;
                        }
                    }
                    catch
                    {
                        // Ignore errors in getting selection, try other methods
                    }

                    try
                    {
                        var documentRange = textPattern.DocumentRange;
                        if (documentRange?.GetAttributeValue(IsSelectionActivePropertyId) is true)
                        {
                            var selected = documentRange.GetText(-1);
                            if (!string.IsNullOrEmpty(selected))
                                return selected;
                        }
                    }
                    catch
                    {
                        // Ignore errors in accessing document range, try other methods
                    }
                }
            }
            catch
            {
                // Ignore errors in TextPattern, try other methods
            }

            // 3) Fallback to LegacyIAccessible selection text
            try
            {
                if (_element.TryGetLegacyIAccessiblePattern() is { Selection: { IsSupported: true, ValueOrDefault: { Length: > 0 } selection } })
                {
                    // UIA maps accSelection to an array of AutomationElements.
                    // This corresponds to VT_DISPATCH (single object) or VT_ARRAY (multiple objects) in MSAA.
                    // Reference.cc Logic:
                    // - VT_DISPATCH: Try accName, then accValue.
                    // - VT_ARRAY: Try accValue of the first element.
                    // We combine these strategies: Check Name/Value of the first element.

                    var selectedItem = selection[0];
                    var itemLegacy = selectedItem.TryGetLegacyIAccessiblePattern();

                    if (itemLegacy != null)
                    {
                        // Try accName
                        if (itemLegacy.Name.IsSupported && !string.IsNullOrEmpty(itemLegacy.Name.ValueOrDefault))
                            return itemLegacy.Name.ValueOrDefault;

                        // Try accValue
                        if (itemLegacy.Value.IsSupported && !string.IsNullOrEmpty(itemLegacy.Value.ValueOrDefault))
                            return itemLegacy.Value.ValueOrDefault;
                    }
                    else
                    {
                        // Fallback if pattern unavailable
                        if (!string.IsNullOrEmpty(selectedItem.Name))
                            return selectedItem.Name;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
        {
            var rect = BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidOperationException("Cannot capture an element with zero width or height.");

            var element = _element;
            var hWnd = element.FrameworkAutomationElement.NativeWindowHandle.ValueOrDefault;
            while (!IsTopLevelHWnd((HWND)hWnd))
            {
                // Get the window position of the toplevel
                element = TreeWalker.GetParent(element);
                if (element == null)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to find the hwnd of the element.");
                }

                hWnd = element.FrameworkAutomationElement.NativeWindowHandle.ValueOrDefault;
            }

            var windowPosition = GetBoundingRectangle(element).To(r => new PixelPoint(r.X, r.Y));
            if (hWnd == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to find the hwnd of the element.");
            }

            return Direct3D11ScreenCapture.CaptureAsync(
                hWnd,
                new PixelRect(
                    rect.X - windowPosition.X,
                    rect.Y - windowPosition.Y,
                    rect.Width,
                    rect.Height),
                cancellationToken);
        }

        #region Interop

        private static unsafe PixelRect GetBoundingRectangle(AutomationElement element)
        {
            try
            {
                var hWnd = element.FrameworkAutomationElement.NativeWindowHandle.ValueOrDefault;
                if (hWnd != 0 && IsTopLevelHWnd((HWND)hWnd))
                {
                    Span<byte> pvAttribute = stackalloc byte[sizeof(RECT)];
                    if (PInvoke.DwmGetWindowAttribute((HWND)hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, pvAttribute) == 0)
                    {
                        var visualRect = Unsafe.As<byte, RECT>(ref MemoryMarshal.GetReference(pvAttribute));
                        return new PixelRect(visualRect.X, visualRect.Y, visualRect.Width, visualRect.Height);
                    }
                }

                return element.BoundingRectangle.To(r => new PixelRect(
                    r.X,
                    r.Y,
                    r.Width,
                    r.Height));
            }
            catch
            {
                return default;
            }
        }

        /// <summary>
        ///     Attempts to find the nearest ancestor element that has a native window handle (HWND).
        /// </summary>
        /// <param name="element"></param>
        /// <param name="hWnd"></param>
        /// <returns></returns>
        private static AutomationElement? TryGetAncestorWithNativeWindowHandle(AutomationElement? element, out nint hWnd)
        {
            while (element != null)
            {
                if (element.FrameworkAutomationElement.NativeWindowHandle.TryGetValue(out hWnd) && hWnd != 0)
                {
                    return element;
                }

                element = TreeWalker.GetParent(element);
            }

            hWnd = 0;
            return null;
        }

        /// <summary>
        ///     Determines if the current element is a top-level window in a Win32 context.
        /// </summary>
        /// <remarks>
        ///     e.g. A control inside a window or a non-win32 element will return false.
        /// </remarks>
        public bool IsTopLevelWindow => IsTopLevelHWnd((HWND)NativeWindowHandle);

        private static bool IsTopLevelHWnd(HWND hWnd)
        {
            if (hWnd == HWND.Null) return false;
            var style = PInvoke.GetWindowLong(hWnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            return (style & (int)WINDOW_STYLE.WS_CHILD) == 0;
        }

        #endregion


        public override bool Equals(object? obj)
        {
            if (obj is not AutomationVisualElementImpl other) return false;
            return Id == other.Id;
        }

        public override int GetHashCode() => Id.GetHashCode();

        public override string ToString()
        {
            try
            {
                return $"({Id}) [{_element.ControlType}] {Name} - {GetText(128)}";
            }
            catch (Exception ex)
            {
                return $"({Id}) [Unknown] - Failed to get element info: {ex.Message}";
            }
        }


        private sealed class SiblingAccessorImpl(AutomationVisualElementImpl visualElement) : VisualElementSiblingAccessor
        {
            protected override IEnumerator<IVisualElement> CreateForwardEnumerator()
            {
                AutomationElement? sibling;
                try
                {
                    sibling = TreeWalker.GetNextSibling(visualElement._element);
                }
                catch
                {
                    yield break;
                }

                while (sibling is not null)
                {
                    AutomationVisualElementImpl item;
                    try
                    {
                        item = new AutomationVisualElementImpl(sibling);
                    }
                    catch
                    {
                        yield break;
                    }

                    yield return item;

                    try
                    {
                        sibling = TreeWalker.GetNextSibling(sibling);
                    }
                    catch
                    {
                        yield break;
                    }
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                AutomationElement? sibling;
                try
                {
                    sibling = TreeWalker.GetPreviousSibling(visualElement._element);
                }
                catch
                {
                    yield break;
                }

                while (sibling is not null)
                {
                    AutomationVisualElementImpl item;
                    try
                    {
                        item = new AutomationVisualElementImpl(sibling);
                    }
                    catch
                    {
                        yield break;
                    }

                    yield return item;

                    try
                    {
                        sibling = TreeWalker.GetPreviousSibling(sibling);
                    }
                    catch
                    {
                        yield break;
                    }
                }
            }
        }
    }
}
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
using Interop.UIAutomationClient;
using Serilog;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    private class AutomationVisualElementImpl(IUIAutomationElement element) : IVisualElement
    {
        private const int IsSelectionActiveAttributeId = 30034;

        public string Id { get; } = GetId(element);

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

                    var parent = TreeWalker.GetParentElement(_element);
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
                IUIAutomationElement? child;
                try
                {
                    child = TreeWalker.GetFirstChildElement(_element);
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
                        child = TreeWalker.GetNextSiblingElement(child);
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
                    return _element.GetCurrentControlTypeOrDefault() switch
                    {
                        UIA_ControlTypeIds.UIA_AppBarControlTypeId => VisualElementType.Menu,
                        UIA_ControlTypeIds.UIA_ButtonControlTypeId => VisualElementType.Button,
                        UIA_ControlTypeIds.UIA_CalendarControlTypeId => VisualElementType.Label,
                        UIA_ControlTypeIds.UIA_CheckBoxControlTypeId => VisualElementType.CheckBox,
                        UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => VisualElementType.ComboBox,
                        UIA_ControlTypeIds.UIA_DataGridControlTypeId => VisualElementType.DataGrid,
                        UIA_ControlTypeIds.UIA_DataItemControlTypeId => VisualElementType.DataGridItem,
                        UIA_ControlTypeIds.UIA_DocumentControlTypeId => VisualElementType.Document,
                        UIA_ControlTypeIds.UIA_EditControlTypeId => VisualElementType.TextEdit,
                        UIA_ControlTypeIds.UIA_GroupControlTypeId => VisualElementType.Panel,
                        UIA_ControlTypeIds.UIA_HeaderControlTypeId or UIA_ControlTypeIds.UIA_HeaderItemControlTypeId => VisualElementType.TableRow,
                        UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => VisualElementType.Hyperlink,
                        UIA_ControlTypeIds.UIA_ImageControlTypeId => VisualElementType.Image,
                        UIA_ControlTypeIds.UIA_ListControlTypeId => VisualElementType.ListView,
                        UIA_ControlTypeIds.UIA_ListItemControlTypeId => VisualElementType.ListViewItem,
                        UIA_ControlTypeIds.UIA_MenuControlTypeId or UIA_ControlTypeIds.UIA_MenuBarControlTypeId => VisualElementType.Menu,
                        UIA_ControlTypeIds.UIA_MenuItemControlTypeId => VisualElementType.MenuItem,
                        UIA_ControlTypeIds.UIA_PaneControlTypeId when IsTopLevelWindow => VisualElementType.TopLevel,
                        UIA_ControlTypeIds.UIA_PaneControlTypeId => VisualElementType.Panel, // a child window, treat as panel
                        UIA_ControlTypeIds.UIA_ProgressBarControlTypeId => VisualElementType.ProgressBar,
                        UIA_ControlTypeIds.UIA_RadioButtonControlTypeId => VisualElementType.RadioButton,
                        UIA_ControlTypeIds.UIA_ScrollBarControlTypeId => VisualElementType.ScrollBar,
                        UIA_ControlTypeIds.UIA_SemanticZoomControlTypeId => VisualElementType.ListView,
                        UIA_ControlTypeIds.UIA_SeparatorControlTypeId => VisualElementType.Unknown,
                        UIA_ControlTypeIds.UIA_SliderControlTypeId or UIA_ControlTypeIds.UIA_SpinnerControlTypeId => VisualElementType.Slider,
                        UIA_ControlTypeIds.UIA_SplitButtonControlTypeId => VisualElementType.Button,
                        UIA_ControlTypeIds.UIA_StatusBarControlTypeId => VisualElementType.Panel,
                        UIA_ControlTypeIds.UIA_TabControlTypeId => VisualElementType.TabControl,
                        UIA_ControlTypeIds.UIA_TabItemControlTypeId => VisualElementType.TabItem,
                        UIA_ControlTypeIds.UIA_TableControlTypeId => VisualElementType.Table,
                        UIA_ControlTypeIds.UIA_TextControlTypeId => VisualElementType.Label,
                        UIA_ControlTypeIds.UIA_ThumbControlTypeId => VisualElementType.Slider,
                        UIA_ControlTypeIds.UIA_TitleBarControlTypeId or UIA_ControlTypeIds.UIA_ToolBarControlTypeId or
                            UIA_ControlTypeIds.UIA_ToolTipControlTypeId => VisualElementType.Panel,
                        UIA_ControlTypeIds.UIA_TreeControlTypeId => VisualElementType.TreeView,
                        UIA_ControlTypeIds.UIA_TreeItemControlTypeId => VisualElementType.TreeViewItem,
                        UIA_ControlTypeIds.UIA_WindowControlTypeId when IsTopLevelWindow => VisualElementType.TopLevel,
                        UIA_ControlTypeIds.UIA_WindowControlTypeId => VisualElementType.Panel, // a child window, treat as panel
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
                    if (_element.GetCurrentIsOffscreenOrDefault())
                        states |= VisualElementStates.Offscreen;
                    if (!_element.GetCurrentIsEnabledOrDefault())
                        states |= VisualElementStates.Disabled;
                    if (_element.GetCurrentHasKeyboardFocusOrDefault())
                        states |= VisualElementStates.Focused;
                    if (_element.TryGetSelectionItemPattern() is { CurrentIsSelected: not 0 })
                        states |= VisualElementStates.Selected;
                    if (_element.TryGetValuePattern() is { CurrentIsReadOnly: not 0 })
                        states |= VisualElementStates.ReadOnly;
                    if (_element.GetCurrentIsPasswordOrDefault())
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
                    var name = _element.GetCurrentNameOrDefault();
                    if (!string.IsNullOrEmpty(name)) return name;
                    if (_element.TryGetLegacyIAccessiblePattern() is { } accessiblePattern) return accessiblePattern.CurrentName;
                    return null;
                }
                catch
                {
                    return null;
                }
            }
        }

        public PixelRect BoundingRectangle => GetBoundingRectangle(_element);

        public int ProcessId { get; } = element.GetCurrentProcessIdOrDefault();

        public nint NativeWindowHandle { get; } = element.GetCurrentNativeWindowHandleOrDefault();

        private readonly IUIAutomationElement _element = element;

        public string? GetText(int maxLength = -1)
        {
            try
            {
                if (_element.TryGetValuePattern() is { } valuePattern) return valuePattern.CurrentValue;
                if (_element.TryGetTextPattern() is { } textPattern) return textPattern.DocumentRange.GetText(maxLength);
                if (_element.TryGetLegacyIAccessiblePattern() is { } accessiblePattern) return accessiblePattern.CurrentValue;
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
                    var state = expandCollapsePattern.CurrentExpandCollapseState;
                    if (state is ExpandCollapseState.ExpandCollapseState_Collapsed or ExpandCollapseState.ExpandCollapseState_PartiallyExpanded)
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
            var windowFromPoint = PInvoke.WindowFromPoint(new Point(point.X, point.Y));
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
                    if (valuePattern.CurrentIsReadOnly != 0)
                    {
                        throw new InvalidOperationException("The target element is read-only and cannot accept text.");
                    }

                    _element.FocusNative();
                    valuePattern.SetValue(text);
                    return;
                }
            }
            catch (COMException ex)
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
                            var selected = GetSelectionText(ranges);
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
                        if (documentRange is not null && IsTruthy(documentRange.GetAttributeValue(IsSelectionActiveAttributeId)))
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
                var selection = _element.TryGetLegacyIAccessiblePattern()?.GetCurrentSelection();
                if (selection is { Length: > 0 })
                {
                    // UIA maps accSelection to an array of AutomationElements.
                    // This corresponds to VT_DISPATCH (single object) or VT_ARRAY (multiple objects) in MSAA.
                    // Reference.cc Logic:
                    // - VT_DISPATCH: Try accName, then accValue.
                    // - VT_ARRAY: Try accValue of the first element.
                    // We combine these strategies: Check Name/Value of the first element.

                    var selectedItem = selection.GetElement(0);
                    var itemLegacy = selectedItem.TryGetLegacyIAccessiblePattern();

                    if (itemLegacy != null)
                    {
                        // Try accName
                        if (!string.IsNullOrEmpty(itemLegacy.CurrentName))
                            return itemLegacy.CurrentName;

                        // Try accValue
                        if (!string.IsNullOrEmpty(itemLegacy.CurrentValue))
                            return itemLegacy.CurrentValue;
                    }
                    else
                    {
                        // Fallback if pattern unavailable
                        var name = selectedItem.GetCurrentNameOrDefault();
                        if (!string.IsNullOrEmpty(name))
                            return name;
                    }
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        // BUG: For a minimized window, the captured image is buggy (but child elements are fine).
        public Task<IVisualElement.ICapturedBitmapData> CaptureAsync(CancellationToken cancellationToken)
        {
            var rect = BoundingRectangle;
            if (rect.Width <= 0 || rect.Height <= 0)
                throw new InvalidOperationException("Cannot capture an element with zero width or height.");

            var element = _element;
            var hWnd = element.GetCurrentNativeWindowHandleOrDefault();
            while (!IsTopLevelHWnd((HWND)hWnd))
            {
                // Get the window position of the toplevel
                element = TreeWalker.GetParentElement(element);
                if (element == null)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to find the hwnd of the element.");
                }

                hWnd = element.GetCurrentNativeWindowHandleOrDefault();
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

        private static string GetId(IUIAutomationElement element)
        {
            var runtimeId = element.TryGetRuntimeId();
            if (runtimeId is { Length: > 0 })
            {
                return string.Join('.', runtimeId.Select(x => x.ToString("X")));
            }

            var bounds = element.GetCurrentBoundingRectangleOrDefault();
            return string.Join(
                '.',
                element.GetCurrentNativeWindowHandleOrDefault().ToString("X"),
                element.GetCurrentControlTypeOrDefault().ToString("X"),
                bounds.left.ToString("X"),
                bounds.top.ToString("X"),
                bounds.right.ToString("X"),
                bounds.bottom.ToString("X"));
        }

        private static string? GetSelectionText(IUIAutomationTextRangeArray ranges)
        {
            var parts = new List<string>(ranges.Length);
            for (var i = 0; i < ranges.Length; i++)
            {
                var text = ranges.GetElement(i).GetText(-1);
                if (!string.IsNullOrEmpty(text))
                {
                    parts.Add(text);
                }
            }

            return parts.Count == 0 ? null : string.Join(null, parts);
        }

        private static bool IsTruthy(object? value) =>
            value switch
            {
                bool boolValue => boolValue,
                int intValue => intValue != 0,
                _ => false
            };

        private static unsafe PixelRect GetBoundingRectangle(IUIAutomationElement element)
        {
            try
            {
                var hWnd = element.GetCurrentNativeWindowHandleOrDefault();
                if (hWnd != 0 && IsTopLevelHWnd((HWND)hWnd))
                {
                    Span<byte> pvAttribute = stackalloc byte[sizeof(RECT)];
                    if (PInvoke.DwmGetWindowAttribute((HWND)hWnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, pvAttribute) == 0)
                    {
                        var visualRect = Unsafe.As<byte, RECT>(ref MemoryMarshal.GetReference(pvAttribute));
                        return new PixelRect(visualRect.X, visualRect.Y, visualRect.Width, visualRect.Height);
                    }
                }

                return element.GetCurrentBoundingRectangleOrDefault().ToPixelRect();
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
        private static IUIAutomationElement? TryGetAncestorWithNativeWindowHandle(IUIAutomationElement? element, out nint hWnd)
        {
            while (element != null)
            {
                if (element.TryGetCurrentNativeWindowHandle(out hWnd))
                {
                    return element;
                }

                element = TreeWalker.GetParentElement(element);
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
                return $"({Id}) [{AutomationExtension.GetControlTypeName(_element.GetCurrentControlTypeOrDefault())}] {Name} - {GetText(128)}";
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
                IUIAutomationElement? sibling;
                try
                {
                    sibling = TreeWalker.GetNextSiblingElement(visualElement._element);
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
                        sibling = TreeWalker.GetNextSiblingElement(sibling);
                    }
                    catch
                    {
                        yield break;
                    }
                }
            }

            protected override IEnumerator<IVisualElement> CreateBackwardEnumerator()
            {
                IUIAutomationElement? sibling;
                try
                {
                    sibling = TreeWalker.GetPreviousSiblingElement(visualElement._element);
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
                        sibling = TreeWalker.GetPreviousSiblingElement(sibling);
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
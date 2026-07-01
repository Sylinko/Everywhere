using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Avalonia;
using Interop.UIAutomationClient;

namespace Everywhere.Windows.Interop;

internal static class AutomationExtension
{
    public const int UnknownControlTypeId = 0;

    extension(IUIAutomationElement element)
    {
        public IUIAutomationInvokePattern? TryGetInvokePattern() =>
            TryGetPattern<IUIAutomationInvokePattern>(element, UIA_PatternIds.UIA_InvokePatternId);

        public IUIAutomationTogglePattern? TryGetTogglePattern() =>
            TryGetPattern<IUIAutomationTogglePattern>(element, UIA_PatternIds.UIA_TogglePatternId);

        public IUIAutomationValuePattern? TryGetValuePattern() =>
            TryGetPattern<IUIAutomationValuePattern>(element, UIA_PatternIds.UIA_ValuePatternId);

        public IUIAutomationTextPattern? TryGetTextPattern() =>
            TryGetPattern<IUIAutomationTextPattern>(element, UIA_PatternIds.UIA_TextPatternId);

        public IUIAutomationExpandCollapsePattern? TryGetExpandCollapsePattern() =>
            TryGetPattern<IUIAutomationExpandCollapsePattern>(element, UIA_PatternIds.UIA_ExpandCollapsePatternId);

        public IUIAutomationSelectionItemPattern? TryGetSelectionItemPattern() =>
            TryGetPattern<IUIAutomationSelectionItemPattern>(element, UIA_PatternIds.UIA_SelectionItemPatternId);

        public IUIAutomationLegacyIAccessiblePattern? TryGetLegacyIAccessiblePattern() =>
            TryGetPattern<IUIAutomationLegacyIAccessiblePattern>(element, UIA_PatternIds.UIA_LegacyIAccessiblePatternId);

        public int[]? TryGetRuntimeId() =>
            TryGet(element.GetRuntimeId);

        public int GetCurrentControlTypeOrDefault() =>
            TryGet(() => element.CurrentControlType, UnknownControlTypeId);

        public bool GetCurrentIsOffscreenOrDefault() =>
            TryGet(() => element.CurrentIsOffscreen, 0) != 0;

        public bool GetCurrentIsEnabledOrDefault() =>
            TryGet(() => element.CurrentIsEnabled, 1) != 0;

        public bool GetCurrentHasKeyboardFocusOrDefault() =>
            TryGet(() => element.CurrentHasKeyboardFocus, 0) != 0;

        public bool GetCurrentIsPasswordOrDefault() =>
            TryGet(() => element.CurrentIsPassword, 0) != 0;

        public string? GetCurrentNameOrDefault() =>
            TryGet(() => element.CurrentName);

        public int GetCurrentProcessIdOrDefault() =>
            TryGet(() => element.CurrentProcessId, -1);

        public nint GetCurrentNativeWindowHandleOrDefault() =>
            TryGet(() => element.CurrentNativeWindowHandle, 0);

        public bool TryGetCurrentNativeWindowHandle(out nint hWnd)
        {
            hWnd = element.GetCurrentNativeWindowHandleOrDefault();
            return hWnd != 0;
        }

        public bool TryGetClickablePoint(out PixelPoint point)
        {
            try
            {
                if (element.GetClickablePoint(out var clickable) != 0)
                {
                    point = new PixelPoint(clickable.x, clickable.y);
                    return true;
                }
            }
            catch (COMException)
            {
            }

            point = default;
            return false;
        }

        public tagRECT GetCurrentBoundingRectangleOrDefault() =>
            TryGet(() => element.CurrentBoundingRectangle, default);

        public void FocusNative()
        {
            var hWnd = element.GetCurrentNativeWindowHandleOrDefault();
            if (hWnd != 0)
            {
                var targetThreadId = PInvoke.GetWindowThreadProcessId((HWND)hWnd, out _);
                var currentThreadId = PInvoke.GetCurrentThreadId();
                var attached = false;

                try
                {
                    if (targetThreadId != 0 && targetThreadId != currentThreadId)
                    {
                        attached = PInvoke.AttachThreadInput(currentThreadId, targetThreadId, true);
                    }

                    PInvoke.SetFocus((HWND)hWnd);
                    return;
                }
                finally
                {
                    if (attached)
                    {
                        PInvoke.AttachThreadInput(currentThreadId, targetThreadId, false);
                    }
                }
            }

            element.SetFocus();
        }
    }

    public static PixelRect ToPixelRect(this tagRECT rect) =>
        new(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);

    public static string GetControlTypeName(int controlTypeId) =>
        controlTypeId switch
        {
            UIA_ControlTypeIds.UIA_AppBarControlTypeId => "AppBar",
            UIA_ControlTypeIds.UIA_ButtonControlTypeId => "Button",
            UIA_ControlTypeIds.UIA_CalendarControlTypeId => "Calendar",
            UIA_ControlTypeIds.UIA_CheckBoxControlTypeId => "CheckBox",
            UIA_ControlTypeIds.UIA_ComboBoxControlTypeId => "ComboBox",
            UIA_ControlTypeIds.UIA_CustomControlTypeId => "Custom",
            UIA_ControlTypeIds.UIA_DataGridControlTypeId => "DataGrid",
            UIA_ControlTypeIds.UIA_DataItemControlTypeId => "DataItem",
            UIA_ControlTypeIds.UIA_DocumentControlTypeId => "Document",
            UIA_ControlTypeIds.UIA_EditControlTypeId => "Edit",
            UIA_ControlTypeIds.UIA_GroupControlTypeId => "Group",
            UIA_ControlTypeIds.UIA_HeaderControlTypeId => "Header",
            UIA_ControlTypeIds.UIA_HeaderItemControlTypeId => "HeaderItem",
            UIA_ControlTypeIds.UIA_HyperlinkControlTypeId => "Hyperlink",
            UIA_ControlTypeIds.UIA_ImageControlTypeId => "Image",
            UIA_ControlTypeIds.UIA_ListControlTypeId => "List",
            UIA_ControlTypeIds.UIA_ListItemControlTypeId => "ListItem",
            UIA_ControlTypeIds.UIA_MenuBarControlTypeId => "MenuBar",
            UIA_ControlTypeIds.UIA_MenuControlTypeId => "Menu",
            UIA_ControlTypeIds.UIA_MenuItemControlTypeId => "MenuItem",
            UIA_ControlTypeIds.UIA_PaneControlTypeId => "Pane",
            UIA_ControlTypeIds.UIA_ProgressBarControlTypeId => "ProgressBar",
            UIA_ControlTypeIds.UIA_RadioButtonControlTypeId => "RadioButton",
            UIA_ControlTypeIds.UIA_ScrollBarControlTypeId => "ScrollBar",
            UIA_ControlTypeIds.UIA_SemanticZoomControlTypeId => "SemanticZoom",
            UIA_ControlTypeIds.UIA_SeparatorControlTypeId => "Separator",
            UIA_ControlTypeIds.UIA_SliderControlTypeId => "Slider",
            UIA_ControlTypeIds.UIA_SpinnerControlTypeId => "Spinner",
            UIA_ControlTypeIds.UIA_SplitButtonControlTypeId => "SplitButton",
            UIA_ControlTypeIds.UIA_StatusBarControlTypeId => "StatusBar",
            UIA_ControlTypeIds.UIA_TabControlTypeId => "Tab",
            UIA_ControlTypeIds.UIA_TabItemControlTypeId => "TabItem",
            UIA_ControlTypeIds.UIA_TableControlTypeId => "Table",
            UIA_ControlTypeIds.UIA_TextControlTypeId => "Text",
            UIA_ControlTypeIds.UIA_ThumbControlTypeId => "Thumb",
            UIA_ControlTypeIds.UIA_TitleBarControlTypeId => "TitleBar",
            UIA_ControlTypeIds.UIA_ToolBarControlTypeId => "ToolBar",
            UIA_ControlTypeIds.UIA_ToolTipControlTypeId => "ToolTip",
            UIA_ControlTypeIds.UIA_TreeControlTypeId => "Tree",
            UIA_ControlTypeIds.UIA_TreeItemControlTypeId => "TreeItem",
            UIA_ControlTypeIds.UIA_WindowControlTypeId => "Window",
            _ => "Unknown"
        };

    private static TPattern? TryGetPattern<TPattern>(IUIAutomationElement element, int patternId) where TPattern : class
    {
        try
        {
            return element.GetCurrentPattern(patternId) as TPattern;
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
    }

    /// <summary>
    /// Try to get a property value using the provided getter function. If a COMException occurs (e.g., due to the element being unavailable), return null instead.
    /// </summary>
    /// <param name="getter"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    private static T? TryGet<T>(Func<T> getter) where T : class
    {
        try
        {
            return getter();
        }
        catch (COMException)
        {
            return null;
        }
    }

    /// <summary>
    /// Try to get a property value using the provided getter function. If a COMException occurs (e.g., due to the element being unavailable), return the specified default value instead.
    /// </summary>
    /// <param name="getter"></param>
    /// <param name="defaultValue"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    private static T TryGet<T>(Func<T> getter, T defaultValue)
    {
        try
        {
            return getter();
        }
        catch (COMException)
        {
            return defaultValue;
        }
    }
}
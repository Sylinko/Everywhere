using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Windows.Extensions;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Windows.Automation;

internal static class UIAutomationQueryExtensions
{
    internal static UIAutomationCacheRequest CreateElementCacheRequest(
        this UIAutomationClient automation,
        VisualElementFields fields,
        bool shouldIncludeRuntimeId = true)
    {
        var options = UIAutomationCacheOptions.ControlType |
            UIAutomationCacheOptions.BoundingRectangle |
            UIAutomationCacheOptions.ProcessId |
            UIAutomationCacheOptions.NativeWindowHandle;

        if (shouldIncludeRuntimeId)
        {
            options |= UIAutomationCacheOptions.RuntimeId;
        }

        if (fields.HasFlag(VisualElementFields.States))
        {
            options |= UIAutomationCacheOptions.IsOffscreen |
                UIAutomationCacheOptions.IsEnabled |
                UIAutomationCacheOptions.HasKeyboardFocus |
                UIAutomationCacheOptions.IsSelected |
                UIAutomationCacheOptions.IsReadOnly |
                UIAutomationCacheOptions.IsPassword;
        }

        if (fields.HasFlag(VisualElementFields.Name))
        {
            options |= UIAutomationCacheOptions.Name;
        }

        if (fields.HasFlag(VisualElementFields.Text))
        {
            options |= UIAutomationCacheOptions.Value | UIAutomationCacheOptions.ValuePattern | UIAutomationCacheOptions.TextPattern;
        }

        return automation.CreateCacheRequest(options);
    }

    internal static VisualElementQueryResult CreateQueryResult(
        this in UIAutomationElement cachedElement,
        UIAutomationVisualElement element,
        VisualElementQueryRequest request)
    {
        var cachedHandle = cachedElement.CachedNativeWindowHandle;
        var requestedFields = request.RequestedFields;
        var availableFields = VisualElementFields.None;
        var type = default(VisualElementType?);
        var states = default(VisualElementStates?);
        var name = default(string);
        var text = default(string);
        var hasMoreText = false;
        var bounds = default(PixelRect?);
        var processId = default(int?);
        var nativeWindowHandle = default(nint?);
        var failure = default(VisualElementQueryFailure);

        try
        {
            if (requestedFields.HasFlag(VisualElementFields.Type))
            {
                type = cachedElement.CachedControlType.ToVisualElementType(UIAutomationVisualElement.IsTopLevelWindow(cachedHandle));
                availableFields |= VisualElementFields.Type;
            }

            if (requestedFields.HasFlag(VisualElementFields.States))
            {
                var value = VisualElementStates.None;
                if (cachedElement.CachedIsOffscreen)
                {
                    value |= VisualElementStates.Offscreen;
                }

                if (!cachedElement.CachedIsEnabled)
                {
                    value |= VisualElementStates.Disabled;
                }

                if (cachedElement.CachedHasKeyboardFocus)
                {
                    value |= VisualElementStates.Focused;
                }

                if (cachedElement.GetCachedIsSelected())
                {
                    value |= VisualElementStates.Selected;
                }

                if (cachedElement.GetCachedIsReadOnly())
                {
                    value |= VisualElementStates.ReadOnly;
                }

                if (cachedElement.CachedIsPassword)
                {
                    value |= VisualElementStates.Password;
                }

                states = value;
                availableFields |= VisualElementFields.States;
            }

            if (requestedFields.HasFlag(VisualElementFields.Name))
            {
                name = cachedElement.GetCachedName();
                availableFields |= VisualElementFields.Name;
            }

            if (requestedFields.HasFlag(VisualElementFields.Bounds))
            {
                bounds = GetBoundingRectangle(in cachedElement, cachedHandle);
                availableFields |= VisualElementFields.Bounds;
            }

            if (requestedFields.HasFlag(VisualElementFields.ProcessId))
            {
                processId = cachedElement.CachedProcessId;
                availableFields |= VisualElementFields.ProcessId;
            }

            if (requestedFields.HasFlag(VisualElementFields.NativeWindowHandle))
            {
                nativeWindowHandle = cachedHandle;
                availableFields |= VisualElementFields.NativeWindowHandle;
            }
        }
        catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            failure = WindowsUIAutomationFailure.CreateFailure(exception);
        }

        if (failure is null && requestedFields.HasFlag(VisualElementFields.Text))
        {
            try
            {
                text = cachedElement.GetCachedValue();
                if (text is not null && text.Length > request.MaxTextCharacters)
                {
                    hasMoreText = true;
                    text = text[..request.MaxTextCharacters];
                }
                else if (text is null)
                {
                    var probeLength = request.MaxTextCharacters == int.MaxValue ? int.MaxValue : request.MaxTextCharacters + 1;
                    text = cachedElement.GetCachedText(probeLength);
                    if (text is not null && text.Length > request.MaxTextCharacters)
                    {
                        hasMoreText = true;
                        text = text[..request.MaxTextCharacters];
                    }
                }

                if (text is not null)
                {
                    availableFields |= VisualElementFields.Text;
                }
            }
            catch (COMException exception) when (WindowsUIAutomationFailure.IsUnsupported(exception))
            {
                // Missing cached text patterns are an explicit field omission, not a provider-wide failure.
            }
            catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
            {
                failure ??= WindowsUIAutomationFailure.CreateFailure(exception);
            }
        }

        var elementId = default(string);
        if (requestedFields.HasFlag(VisualElementFields.Id))
        {
            elementId = element.Id;
            availableFields |= VisualElementFields.Id;
        }

        var snapshot = new VisualElementSnapshot(elementId, type, states, name, text, hasMoreText, bounds, processId, nativeWindowHandle);
        return new VisualElementQueryResult(element, snapshot, availableFields, requestedFields & ~availableFields, failure);
    }

    private static unsafe PixelRect GetBoundingRectangle(in UIAutomationElement element, nint nativeWindowHandle)
    {
        if (UIAutomationVisualElement.IsTopLevelWindow(nativeWindowHandle))
        {
            var visualRectangle = default(RECT);
            if (PInvoke.DwmGetWindowAttribute(
                    (HWND)nativeWindowHandle,
                    DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                    &visualRectangle,
                    (uint)sizeof(RECT)).Succeeded)
            {
                return new PixelRect(visualRectangle.X, visualRectangle.Y, visualRectangle.Width, visualRectangle.Height);
            }
        }

        var rectangle = element.GetCachedBoundingRectangle();
        return new PixelRect(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
    }
}
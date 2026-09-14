using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.Windows.Automation;

/// <summary>Combines Win32 window stacking with UI Automation point acquisition for Host-owned picker sessions.</summary>
[InHostProcess(ProcessRole.Automation)]
public sealed class WindowsVisualPickerResolver : IVisualPickerResolver
{
    /// <inheritdoc />
    public VisualElementQueryResult? Resolve(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        ScreenSelectionMode mode,
        int excludedProcessId,
        VisualElementQueryRequest request)
    {
        var result = mode switch
        {
            ScreenSelectionMode.Screen => backend.Query(retention, VisualElementLocator.FromPoint(point), VisualElementResolution.Screen, request),
            ScreenSelectionMode.Window => QueryWindow(backend, retention, point, excludedProcessId, request),
            ScreenSelectionMode.Element => backend.Query(retention, VisualElementLocator.FromPoint(point), VisualElementResolution.Direct, request),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "The visual picker does not support free-form selection."),
        };
        return result?.Snapshot.ProcessId == excludedProcessId ? null : result;
    }

    private static unsafe VisualElementQueryResult? QueryWindow(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        int excludedProcessId,
        VisualElementQueryRequest request)
    {
        var targetWindow = HWND.Null;
        PInvoke.EnumWindows(
            (window, _) =>
            {
                if (!PInvoke.IsWindowVisible(window) || PInvoke.IsIconic(window)) return true;
                PInvoke.GetWindowThreadProcessId(window, out var processId);
                if (processId == excludedProcessId) return true;

                var extendedStyle = PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
                if ((extendedStyle & (int)WINDOW_EX_STYLE.WS_EX_NOACTIVATE) != 0) return true;

                var isCloaked = 0L;
                PInvoke.DwmGetWindowAttribute(window, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &isCloaked, sizeof(long));
                if (isCloaked != 0) return true;

                if (!PInvoke.GetWindowRect(window, out var bounds) ||
                    point.X < bounds.left || point.X >= bounds.right ||
                    point.Y < bounds.top || point.Y >= bounds.bottom)
                {
                    return true;
                }

                targetWindow = window;
                return false;
            },
            0);

        return targetWindow.IsNull ?
            null :
            backend.Query(retention, VisualElementLocator.FromNativeWindow(targetWindow), VisualElementResolution.Direct, request);
    }
}
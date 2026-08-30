using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using ZLinq;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Represents one immutable process-wide observation of the Windows display topology.
/// </summary>
/// <remarks>
/// The initial topology is captured on first use. Each <c>WM_DISPLAYCHANGE</c> notification creates a new generation and atomically replaces <see cref="Current" />; no polling or structural comparison is performed.
/// </remarks>
public sealed class WindowsDisplayTopology
{
    /// <summary>
    /// Gets the current process-wide display-topology snapshot.
    /// </summary>
    public static WindowsDisplayTopology Current => Volatile.Read(ref _current);

    /// <summary>
    /// Gets the generation advanced by each received <c>WM_DISPLAYCHANGE</c> notification.
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// Gets the displays ordered from top to bottom and then from left to right.
    /// </summary>
    public IReadOnlyList<WindowsDisplay> Displays { get; }

    /// <summary>
    /// Gets the primary display, or the first display when Windows did not identify a primary display.
    /// </summary>
    public WindowsDisplay? Primary =>
        Displays.AsValueEnumerable().FirstOrDefault(static display => display.IsPrimary) ?? Displays.AsValueEnumerable().FirstOrDefault();

    private const uint PrimaryMonitorFlag = 1;

    private static WindowsDisplayTopology _current;

    static WindowsDisplayTopology()
    {
        MessageWindow.Shared.AddHandler((uint)WINDOW_MESSAGE.WM_DISPLAYCHANGE, HandleDisplayChanged);
        _current = Capture(1);
    }

    private WindowsDisplayTopology(long generation, WindowsDisplay[] displays)
    {
        Generation = generation;
        Displays = Array.AsReadOnly(displays);
    }

    public WindowsDisplay? Find(nint monitorHandle) =>
        Displays.AsValueEnumerable().FirstOrDefault(display => display.MonitorHandle == monitorHandle);

    public WindowsDisplay? FindNearest(PixelPoint point)
    {
        WindowsDisplay? nearest = null;
        var nearestDistance = long.MaxValue;
        foreach (var display in Displays)
        {
            var bounds = display.Bounds;
            var right = bounds.Right - 1;
            var bottom = bounds.Bottom - 1;
            var deltaX = point.X < bounds.X ? bounds.X - point.X : point.X > right ? point.X - right : 0;
            var deltaY = point.Y < bounds.Y ? bounds.Y - point.Y : point.Y > bottom ? point.Y - bottom : 0;
            var distance = (long)deltaX * deltaX + (long)deltaY * deltaY;
            if (distance >= nearestDistance)
            {
                continue;
            }

            nearest = display;
            nearestDistance = distance;
        }

        return nearest;
    }

    internal WindowsDisplay? FindTopLevelWindowDisplay(HWND windowHandle)
    {
        if (PInvoke.GetAncestor(windowHandle, GET_ANCESTOR_FLAGS.GA_ROOTOWNER) != windowHandle || !PInvoke.IsWindowVisible(windowHandle))
        {
            return null;
        }

        var placement = new WINDOWPLACEMENT();
        if (!PInvoke.GetWindowPlacement(windowHandle, ref placement) || placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMINIMIZED)
        {
            return null;
        }

        var monitorHandle = PInvoke.MonitorFromWindow(windowHandle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        return monitorHandle == HMONITOR.Null ? null : Find(monitorHandle);
    }

    private static void HandleDisplayChanged(in MSG _)
    {
        var currentGeneration = _current.Generation;
        Volatile.Write(ref _current, Capture(currentGeneration + 1));
    }

    private static unsafe WindowsDisplayTopology Capture(long generation)
    {
        var displays = new List<WindowsDisplay>();
        if (!PInvoke.EnumDisplayMonitors(
                HDC.Null,
                null,
                (monitorHandle, _, _, _) =>
                {
                    var monitorInfo = new MONITORINFO { cbSize = checked((uint)Marshal.SizeOf<MONITORINFO>()) };
                    if (!PInvoke.GetMonitorInfo(monitorHandle, ref monitorInfo))
                    {
                        return false;
                    }

                    displays.Add(
                        new WindowsDisplay(
                            monitorHandle,
                            new PixelRect(
                                monitorInfo.rcMonitor.X,
                                monitorInfo.rcMonitor.Y,
                                monitorInfo.rcMonitor.Width,
                                monitorInfo.rcMonitor.Height),
                            (monitorInfo.dwFlags & PrimaryMonitorFlag) != 0));
                    return true;
                },
                0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to enumerate displays.");
        }

        displays.Sort(static (left, right) =>
        {
            var comparison = left.Bounds.Y.CompareTo(right.Bounds.Y);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Bounds.X.CompareTo(right.Bounds.X);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Bounds.Width.CompareTo(right.Bounds.Width);
            if (comparison != 0)
            {
                return comparison;
            }

            comparison = left.Bounds.Height.CompareTo(right.Bounds.Height);
            return comparison != 0 ? comparison : left.MonitorHandle.CompareTo(right.MonitorHandle);
        });

        return new WindowsDisplayTopology(generation, [.. displays]);
    }
}

/// <summary>
/// Describes one display inside an immutable topology observation.
/// </summary>
/// <param name="MonitorHandle">The borrowed Win32 monitor pseudo-handle.</param>
/// <param name="Bounds">The display bounds in physical virtual-screen pixels.</param>
/// <param name="IsPrimary">Whether Windows identifies this display as the primary display.</param>
public sealed record WindowsDisplay(nint MonitorHandle, PixelRect Bounds, bool IsPrimary);
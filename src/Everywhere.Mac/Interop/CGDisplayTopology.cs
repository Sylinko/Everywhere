using Avalonia;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Represents one immutable process-wide observation of the macOS display topology.
/// </summary>
/// <remarks>
/// The application initializes this type on its main thread after <see cref="NSApplication.Init" />. Each
/// <see cref="NSApplication.DidChangeScreenParametersNotification" /> creates a new generation and atomically replaces
/// <see cref="Current" />; no polling or structural comparison is performed.
/// </remarks>
public sealed class CGDisplayTopology
{
    /// <summary>
    /// Gets the current process-wide display-topology snapshot.
    /// </summary>
    public static CGDisplayTopology Current => Volatile.Read(ref _current);

    /// <summary>
    /// Gets the generation advanced by each received screen-parameters notification.
    /// </summary>
    public long Generation { get; }

    /// <summary>
    /// Gets the displays ordered from top to bottom and then from left to right in AX screen coordinates.
    /// </summary>
    public IReadOnlyList<MacDisplay> Displays { get; }

    /// <summary>
    /// Gets the primary display containing the menu bar, or the first display when AppKit did not identify one.
    /// </summary>
    public MacDisplay? Primary => Displays.FirstOrDefault(static display => display.IsPrimary) ?? Displays.FirstOrDefault();

    private static CGDisplayTopology _current;

    // ReSharper disable once NotAccessedField.Local
    // The process-lifetime token keeps the strongly typed notification subscription active.
    private static readonly NSObject ScreenParametersObserver;

    static CGDisplayTopology()
    {
        ScreenParametersObserver = NSApplication.Notifications.ObserveDidChangeScreenParameters(HandleScreenParametersChanged);
        _current = Capture(1);
    }

    private CGDisplayTopology(long generation, MacDisplay[] displays)
    {
        Generation = generation;
        Displays = Array.AsReadOnly(displays);
    }

    /// <summary>
    /// Initializes display observation. Call this on the application main thread after <see cref="NSApplication.Init" />.
    /// </summary>
    public static void Initialize() => _ = Current;

    /// <summary>
    /// Finds a display by its Core Graphics display identifier.
    /// </summary>
    /// <param name="displayId">The Core Graphics display identifier.</param>
    /// <returns>The display in this topology snapshot, or <see langword="null" /> when it is absent.</returns>
    public MacDisplay? Find(uint displayId) => Displays.FirstOrDefault(display => display.DisplayId == displayId);

    /// <summary>
    /// Finds the display containing a point, or the nearest display when the point lies outside every display.
    /// </summary>
    /// <param name="point">The point in AX screen coordinates.</param>
    /// <returns>The nearest display, or <see langword="null" /> when the topology contains no displays.</returns>
    public MacDisplay? FindNearest(PixelPoint point)
    {
        MacDisplay? nearest = null;
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

    /// <summary>
    /// Finds the display containing the largest visible area of a top-level window.
    /// </summary>
    /// <param name="bounds">The top-level window bounds in AX screen coordinates.</param>
    /// <returns>The display with the largest intersection, or <see langword="null" /> when the window does not intersect a display.</returns>
    internal MacDisplay? FindTopLevelWindowDisplay(PixelRect bounds)
    {
        MacDisplay? bestDisplay = null;
        var bestArea = 0L;
        foreach (var display in Displays)
        {
            var left = Math.Max(bounds.X, display.Bounds.X);
            var top = Math.Max(bounds.Y, display.Bounds.Y);
            var right = Math.Min(bounds.Right, display.Bounds.Right);
            var bottom = Math.Min(bounds.Bottom, display.Bounds.Bottom);
            var width = Math.Max(0, right - left);
            var height = Math.Max(0, bottom - top);
            var area = (long)width * height;
            if (area <= bestArea)
            {
                continue;
            }

            bestDisplay = display;
            bestArea = area;
        }

        return bestDisplay;
    }

    private static void HandleScreenParametersChanged(object? sender, NSNotificationEventArgs args)
    {
        var generation = checked(Current.Generation + 1);
        Volatile.Write(ref _current, Capture(generation));
    }

    private static CGDisplayTopology Capture(long generation)
    {
        var screens = NSScreen.Screens;
        if (screens.Length == 0)
        {
            return new CGDisplayTopology(generation, []);
        }

        var primaryScreen = screens[0];
        var primaryHeight = primaryScreen.Frame.Height;
        var displays = new MacDisplay[screens.Length];
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            var displayId = GetDisplayId(screen);
            var frame = screen.Frame;
            displays[index] = new MacDisplay(
                displayId,
                new PixelRect((int)frame.X, (int)(primaryHeight - (frame.Y + frame.Height)), (int)frame.Width, (int)frame.Height),
                screen.LocalizedName,
                ReferenceEquals(screen, primaryScreen));
        }

        Array.Sort(
            displays,
            static (left, right) =>
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
                return comparison != 0 ? comparison : left.DisplayId.CompareTo(right.DisplayId);
            });

        return new CGDisplayTopology(generation, displays);
    }

    private static uint GetDisplayId(NSScreen screen)
    {
        var displayId = (screen.DeviceDescription["NSScreenNumber"] as NSNumber)?.UInt32Value ?? 0;
        return displayId != 0 ?
            displayId :
            throw new InvalidOperationException("AppKit returned a screen without a Core Graphics display identifier.");
    }
}

/// <summary>
/// Describes one display inside an immutable macOS topology observation.
/// </summary>
/// <param name="DisplayId">The Core Graphics display identifier.</param>
/// <param name="Bounds">The display bounds in AX screen coordinates.</param>
/// <param name="Name">The localized display name captured with this observation.</param>
/// <param name="IsPrimary">Whether this display was first in <see cref="NSScreen.Screens" />.</param>
public sealed record MacDisplay(uint DisplayId, PixelRect Bounds, string Name, bool IsPrimary);
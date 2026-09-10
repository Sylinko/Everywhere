using System.Runtime.InteropServices;
using Avalonia;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Represents one operation-local observation of visible macOS windows in Quartz front-to-back order.
/// </summary>
public sealed partial class CGWindowZOrder
{
    /// <summary>
    /// Describes one visible Quartz window assigned to exactly one display in an operation-local Z-order observation.
    /// </summary>
    public sealed record Entry(uint WindowId, int OwnerProcessId, PixelRect Bounds, MacDisplay Display);

    public IReadOnlyList<Entry> Windows { get; }

    private const int MaximumWindowCount = 4_096;

    // These wrappers are process-lifetime Core Foundation dictionary keys. Do not dispose them or aliases to them.
    private static readonly NSString WindowNumberKey = new("kCGWindowNumber");
    private static readonly NSString WindowOwnerProcessIdKey = new("kCGWindowOwnerPID");
    private static readonly NSString WindowBoundsKey = new("kCGWindowBounds");

    private CGWindowZOrder(Entry[] windows)
    {
        Windows = Array.AsReadOnly(windows);
    }

    public static CGWindowZOrder Capture(CGDisplayTopology topology)
    {
        var windowInfo = CGInterop.CGWindowListCopyWindowInfo(
            CGWindowListOption.OnScreenOnly | CGWindowListOption.ExcludeDesktopElements,
            0);
        if (windowInfo == 0)
        {
            throw new InvalidOperationException("Core Graphics did not return the visible window list.");
        }

        try
        {
            var count = CFArrayGetCount(windowInfo);
            if (count is < 0 or > MaximumWindowCount)
            {
                throw new InvalidOperationException($"The visible macOS window list exceeded the {MaximumWindowCount}-window safety limit.");
            }

            var windows = new List<Entry>((int)count);
            var observedWindowIds = new HashSet<uint>();
            var userApplicationEligibility = new Dictionary<int, bool>();
            for (nint index = 0; index < count; index++)
            {
                var dictionary = CFArrayGetValueAtIndex(windowInfo, index);
                if (!TryReadInt64(dictionary, WindowNumberKey, out var windowIdValue) ||
                    windowIdValue is <= 0 or > uint.MaxValue ||
                    !TryReadInt64(dictionary, WindowOwnerProcessIdKey, out var processIdValue) ||
                    processIdValue is <= 0 or > int.MaxValue ||
                    !TryReadBounds(dictionary, out var bounds))
                {
                    continue;
                }

                var windowId = (uint)windowIdValue;
                var processId = (int)processIdValue;
                if (!IsUserApplication(processId, userApplicationEligibility))
                {
                    continue;
                }

                var display = topology.FindTopLevelWindowDisplay(bounds);
                if (display is null || !observedWindowIds.Add(windowId))
                {
                    continue;
                }

                windows.Add(new Entry(windowId, processId, bounds, display));
            }

            return new CGWindowZOrder([.. windows]);
        }
        finally
        {
            CFInterop.CFRelease(windowInfo);
        }
    }

    public Entry? Find(uint windowId) => Windows.FirstOrDefault(window => window.WindowId == windowId);

    public static bool TryGetOwnerProcessId(uint windowId, out int processId)
    {
        processId = 0;
        if (windowId == 0)
        {
            return false;
        }

        var windowInfo = CGInterop.CGWindowListCopyWindowInfo(CGWindowListOption.IncludingWindow, windowId);
        if (windowInfo == 0)
        {
            return false;
        }

        try
        {
            return CFArrayGetCount(windowInfo) > 0 &&
                TryReadInt64(CFArrayGetValueAtIndex(windowInfo, 0), WindowOwnerProcessIdKey, out var processIdValue) &&
                processIdValue is > 0 and <= int.MaxValue;
        }
        finally
        {
            CFInterop.CFRelease(windowInfo);
        }
    }

    private static bool IsUserApplication(int processId, Dictionary<int, bool> eligibility)
    {
        if (eligibility.TryGetValue(processId, out var isEligible))
        {
            return isEligible;
        }

        // Quartz also reports compositor-owned surfaces such as Window Server overlays. Their owner PIDs are
        // not user applications and querying AXWindows on them can consume a complete messaging timeout.
        // NSRunningApplication intentionally tracks user applications only, while retaining Regular and
        // Accessory/LSUIElement applications and their nonzero-level floating AXWindows.
        using var application = NSRunningApplication.GetRunningApplication(processId);
        isEligible = application is { Terminated: false };
        eligibility.Add(processId, isEligible);
        return isEligible;
    }

    private static bool TryReadInt64(nint dictionary, NSString key, out long value)
    {
        value = 0;
        var number = dictionary == 0 ? 0 : CFDictionaryGetValue(dictionary, key.Handle.Handle);
        return number != 0 && CFGetTypeID(number) == CFNumberGetTypeID() && CFNumberGetValue(number, CFNumberType.SInt64, out value) != 0;
    }

    private static bool TryReadBounds(nint dictionary, out PixelRect bounds)
    {
        bounds = default;
        var value = dictionary == 0 ? 0 : CFDictionaryGetValue(dictionary, WindowBoundsKey.Handle.Handle);
        if (value == 0 || CGRectMakeWithDictionaryRepresentation(value, out var rectangle) == 0 || rectangle.Width <= 0 || rectangle.Height <= 0)
        {
            return false;
        }

        bounds = new PixelRect((int)rectangle.X, (int)rectangle.Y, (int)rectangle.Width, (int)rectangle.Height);
        return bounds is { Width: > 0, Height: > 0 };
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dictionary, nint key);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFGetTypeID(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial byte CFNumberGetValue(nint number, CFNumberType numberType, out long value);

    [DllImport(CoreGraphics)]
    private static extern byte CGRectMakeWithDictionaryRepresentation(nint dictionary, out CGRect rectangle);

    private enum CFNumberType
    {
        SInt64 = 4,
    }
}
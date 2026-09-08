using System.Runtime.InteropServices;
using CoreFoundation;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

internal static partial class SkyLightInterop
{
    // https://github.com/lwouis/alt-tab-macos/blob/master/src/api-wrappers/private-apis/SkyLight.framework.swift
    private const string SkyLight = "/System/Library/PrivateFrameworks/SkyLight.framework/SkyLight";

    public static uint CGSMainConnection { get; } = CGSMainConnectionID();

    public static unsafe CGImage? CaptureWindowToRect(
        uint windowId,
        CGRect rect,
        CGSWindowCaptureOptions options)
    {
        var result = CGSCaptureWindowsContentsToRectWithOptions(
            CGSMainConnection,
            &windowId,
            true,
            rect.X,
            rect.Y,
            rect.Width,
            rect.Height,
            options,
            out var imageOut);

        if (result != AXError.Success || imageOut == 0)
            return null;

        return Runtime.GetINativeObject<CGImage>(imageOut, true);
    }

    public static CGImage? HardwareCaptureWindowList(uint[] windowList, CGSWindowCaptureOptions options)
    {
        unsafe
        {
            fixed (uint* windowListPtr = windowList)
            {
                var windowArrayPtr = CGSHWCaptureWindowList(
                    CGSMainConnection,
                    windowListPtr,
                    (nuint)windowList.Length,
                    options);

                if (windowArrayPtr == 0)
                    return null;

                // CFArray.ArrayFromHandle returns a managed array of content (CGImage objects)
                // but does not release the CFArrayRef itself. We must release it to avoid a memory leak.
                CGImage?[]? windowArray;
                try
                {
                    windowArray = CFArray.ArrayFromHandle<CGImage>(windowArrayPtr);
                }
                finally
                {
                    CFInterop.CFRelease(windowArrayPtr);
                }
                
                return windowArray?[0];
            }
        }
    }

    /// <summary>
    /// Returns the connection to the WindowServer. This connection ID is required when calling other APIs
    /// </summary>
    /// <returns></returns>
    [LibraryImport(SkyLight)]
    private static partial uint CGSMainConnectionID();

    [LibraryImport(SkyLight)]
    private static unsafe partial AXError CGSCaptureWindowsContentsToRectWithOptions(
        uint cid,
        uint* wid,
        [MarshalAs(UnmanagedType.Bool)] bool windowOnly,
        double x,
        double y,
        double width,
        double height,
        CGSWindowCaptureOptions options,
        out nint imageOut);

    [LibraryImport(SkyLight)]
    private static unsafe partial nint CGSHWCaptureWindowList(uint cid, uint* windowList, nuint windowCount, CGSWindowCaptureOptions options);

    [Flags]
    public enum CGSWindowCaptureOptions
    {
        IgnoreGlobalCLipShape = 1 << 11,

        /// <summary>
        /// Requests nominal resolution. At a 2x backing scale this is approximately half the width/height and one quarter of the pixel count of best resolution.
        /// </summary>
        NominalResolution = 1 << 9,

        /// <summary>Requests best available image resolution; this changes bitmap density, not desktop coordinates.</summary>
        BestResolution = 1 << 8,

        /// <summary>
        /// when Stage Manager is enabled, screenshots can become skewed. This param gets us full-size screenshots regardless
        /// </summary>
        FullSize = 1 << 19
    }
}
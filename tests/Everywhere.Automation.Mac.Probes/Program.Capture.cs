using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Platform;
using CoreGraphics;
using Everywhere.Automation;
using Everywhere.Mac.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Automation.Mac.Probes;

public static partial class Program
{
    private const string CoreGraphicsFramework = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    private static CaptureContractObservation ProbeCaptureContract()
    {
        using var provider = StartProvider();
        var errorOutputTask = provider.StandardError.ReadToEndAsync();
        try
        {
            var providerProcessId = ReadProviderProcessId(provider);
            var status = SendProviderCommandAndRead(provider, "prepare-capture", "CAPTURE", 2);
            var providerObservation = JsonSerializer.Deserialize<CaptureProviderObservation>(status[1]) ??
                throw new InvalidOperationException("The capture provider returned an invalid observation.");
            Require(providerObservation.Points.Count == 4, "The capture provider did not return four quadrant points.");

            // Let Window Server commit the newly displayed colored surface before capturing it.
            Thread.Sleep(300);

            using var backend = new MacVisualElementBackend();
            using var context = new VisualContext();
            using var retention = context.CreateRetention();
            var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Bounds, 0);
            var window = backend.Query(
                retention,
                VisualElementLocator.FromNativeWindow((nint)providerObservation.WindowId),
                VisualElementResolution.Direct,
                request) ?? throw new InvalidOperationException("The controlled capture window could not be resolved from its Quartz ID.");
            Require(window.IsSuccess, "The controlled capture window query failed.");
            var windowBounds = window.Snapshot.Bounds ?? throw new InvalidOperationException("The controlled capture window did not report AX bounds.");

            using var windowCapture = window.Element.CaptureAsync().GetAwaiter().GetResult();
            var pixelFormat = windowCapture.Format;
            var alphaFormat = windowCapture.AlphaFormat;
            var windowObservation = ObserveCapture(windowCapture, providerObservation.Points, providerObservation.BackingScaleFactor);

            var topLeft = providerObservation.Points.Single(static point => point.Name == "capture-top-left");
            var child = QueryElementAtPointForProcess(
                backend,
                retention,
                providerProcessId,
                new PixelPoint(checked((int)topLeft.X), checked((int)topLeft.Y)),
                VisualElementResolution.Direct,
                request) ?? throw new InvalidOperationException("The top-left capture quadrant could not be acquired by AX hit testing.");
            Require(child.IsSuccess, "The top-left capture quadrant query failed.");
            var childBounds = child.Snapshot.Bounds ?? throw new InvalidOperationException("The top-left capture quadrant did not report AX bounds.");
            using var childCapture = child.Element.CaptureAsync().GetAwaiter().GetResult();
            var childObservation = ObserveCapture(childCapture, [topLeft], providerObservation.BackingScaleFactor);

            var occludedStatus = SendProviderCommandAndRead(provider, "occlude-capture", "OCCLUDED_CAPTURE", 2);
            var occludedProviderObservation = DeserializeCaptureProviderObservation(occludedStatus[1], "occluded-window");
            Thread.Sleep(300);
            var occludedZOrder = CGWindowZOrder.Capture(CGDisplayTopology.Current).Windows;
            var targetZOrderIndex = FindWindowIndex(occludedZOrder, providerObservation.WindowId);
            var occluderZOrderIndex = occludedProviderObservation.OccluderWindowId is { } occluderWindowId ? FindWindowIndex(occludedZOrder, occluderWindowId) : -1;
            var occludedWindowCapture = TryObserveCapture(
                () => window.Element.CaptureAsync().GetAwaiter().GetResult(),
                occludedProviderObservation.Points,
                providerObservation.BackingScaleFactor);
            var occludedWindowObservation = new OccludedWindowCaptureObservation(
                occludedProviderObservation,
                targetZOrderIndex,
                occluderZOrderIndex,
                targetZOrderIndex >= 0 && occluderZOrderIndex >= 0 && occluderZOrderIndex < targetZOrderIndex,
                occludedWindowCapture);

            SendProviderCommandAndRead(provider, "reveal-capture", "REVEALED_CAPTURE", 2);
            Thread.Sleep(300);

            var partiallyOffscreenStatus = SendProviderCommandAndRead(provider, "move-capture-partially-offscreen", "PARTIALLY_OFFSCREEN_CAPTURE", 2);
            var partiallyOffscreenProviderObservation = DeserializeCaptureProviderObservation(partiallyOffscreenStatus[1], "partially-offscreen window");
            Thread.Sleep(300);
            var partiallyOffscreenWindowObservation = ObserveOffscreenCapture(
                backend,
                retention,
                window,
                request,
                partiallyOffscreenProviderObservation,
                providerObservation.BackingScaleFactor);

            var fullyOffscreenStatus = SendProviderCommandAndRead(provider, "move-capture-fully-offscreen", "FULLY_OFFSCREEN_CAPTURE", 2);
            var fullyOffscreenProviderObservation = DeserializeCaptureProviderObservation(fullyOffscreenStatus[1], "fully-offscreen window");
            Thread.Sleep(300);
            var fullyOffscreenWindowObservation = ObserveOffscreenCapture(
                backend,
                retention,
                window,
                request,
                fullyOffscreenProviderObservation,
                providerObservation.BackingScaleFactor);

            SendProviderCommandAndRead(provider, "restore-capture-position", "RESTORED_CAPTURE_POSITION", 2);
            Thread.Sleep(300);

            SendProviderCommandAndRead(provider, "minimize-capture", "MINIMIZED_CAPTURE", 2);
            Thread.Sleep(800);
            var minimizedWindowCapture = TryObserveCapture(
                () => window.Element.CaptureAsync().GetAwaiter().GetResult(),
                providerObservation.Points,
                providerObservation.BackingScaleFactor);

            SendProviderCommandAndRead(provider, "restore-capture", "RESTORED_CAPTURE", 2);
            Thread.Sleep(300);
            var borderlessStatus = SendProviderCommandAndRead(provider, "prepare-capture-borderless", "CAPTURE", 2);
            var borderlessProviderObservation = JsonSerializer.Deserialize<CaptureProviderObservation>(borderlessStatus[1]) ??
                throw new InvalidOperationException("The capture provider returned an invalid borderless-window observation.");
            Thread.Sleep(300);
            var borderlessWindowCapture = TryObserveCapture(
                () =>
                {
                    var borderlessWindow = backend.Query(
                        retention,
                        VisualElementLocator.FromNativeWindow((nint)borderlessProviderObservation.WindowId),
                        VisualElementResolution.Direct,
                        request) ?? throw new InvalidOperationException("The borderless capture window could not be resolved from its Quartz ID.");
                    return borderlessWindow.Element.CaptureAsync().GetAwaiter().GetResult();
                },
                borderlessProviderObservation.Points,
                borderlessProviderObservation.BackingScaleFactor);

            var screen = backend.Query(
                retention,
                VisualElementLocator.FromNativeWindow((nint)borderlessProviderObservation.WindowId),
                VisualElementResolution.Screen,
                request) ?? throw new InvalidOperationException("The controlled capture window's Screen could not be resolved.");
            Require(screen.IsSuccess, "The controlled capture window's Screen query failed.");
            var screenBounds = screen.Snapshot.Bounds ?? throw new InvalidOperationException("The controlled capture window's Screen did not report bounds.");
            var isScreenCaptureAuthorized = CGPreflightScreenCaptureAccess();
            var screenCapture = TryObserveCapture(
                () => screen.Element.CaptureAsync().GetAwaiter().GetResult(),
                borderlessProviderObservation.Points,
                null);
            var directScreenCapture = TryObserveCapture(
                () => CaptureScreenWithExplicitWindowList(screenBounds),
                borderlessProviderObservation.Points,
                null);

            var observation = new CaptureContractObservation(
                providerProcessId,
                providerObservation.WindowId,
                providerObservation.BackingScaleFactor,
                windowBounds,
                childBounds,
                pixelFormat,
                alphaFormat,
                windowObservation,
                childObservation,
                occludedWindowObservation,
                partiallyOffscreenWindowObservation,
                fullyOffscreenWindowObservation,
                minimizedWindowCapture,
                borderlessWindowCapture,
                isScreenCaptureAuthorized,
                screenCapture,
                directScreenCapture,
                IsPixelFormatDeclaredCorrectly: pixelFormat == PixelFormat.Rgba8888 && alphaFormat == AlphaFormat.Premul,
                DoesWindowSurfaceMatchAxBounds: windowObservation.DoesDensityMatchExpected,
                IsWindowOrientationAndChannelOrderCorrect: windowObservation.AreExpectedColorsObserved,
                IsChildCropCorrect: childObservation.AreExpectedColorsObserved && childCapture.Bounds == childBounds,
                IsOccludedWindowCaptureCorrect:
                    occludedWindowObservation.Provider.IsOccluded &&
                    occludedWindowObservation.IsOccluderAheadInZOrder &&
                    occludedWindowCapture.Observation is { DoesDensityMatchExpected: true, AreExpectedColorsObserved: true },
                IsPartiallyOffscreenWindowCaptureCorrect:
                    partiallyOffscreenWindowObservation.Provider.VisibleAreaRatio is > 0 and < 1 &&
                    partiallyOffscreenWindowObservation.IsScreenResolved &&
                    partiallyOffscreenWindowObservation.IsNativeWindowReacquired &&
                    partiallyOffscreenWindowObservation.RetainedElementCapture.Observation is { DoesDensityMatchExpected: true, AreExpectedColorsObserved: true } &&
                    partiallyOffscreenWindowObservation.ReacquiredElementCapture.Observation is { DoesDensityMatchExpected: true, AreExpectedColorsObserved: true },
                IsFullyOffscreenWindowCaptureUnavailableAsObserved:
                    fullyOffscreenWindowObservation.Provider.VisibleAreaRatio == 0 &&
                    !fullyOffscreenWindowObservation.IsScreenResolved &&
                    fullyOffscreenWindowObservation.IsNativeWindowReacquired &&
                    fullyOffscreenWindowObservation.RetainedElementCapture is { Observation: null, Error: not null } &&
                    fullyOffscreenWindowObservation.ReacquiredElementCapture is { Observation: null, Error: not null },
                IsMinimizedWindowCaptureCorrect: minimizedWindowCapture.Observation is { DoesDensityMatchExpected: true, AreExpectedColorsObserved: true },
                IsBorderlessWindowCaptureCorrect: borderlessWindowCapture.Observation is { DoesDensityMatchExpected: true, AreExpectedColorsObserved: true },
                IsScreenOrientationAndChannelOrderCorrect: isScreenCaptureAuthorized && screenCapture.Observation is { AreExpectedColorsObserved: true },
                IsExplicitWindowListScreenCaptureCorrect: directScreenCapture.Observation is { AreExpectedColorsObserved: true });

            if (!observation.IsPixelFormatDeclaredCorrectly ||
                !observation.DoesWindowSurfaceMatchAxBounds ||
                !observation.IsWindowOrientationAndChannelOrderCorrect ||
                !observation.IsChildCropCorrect ||
                !observation.IsOccludedWindowCaptureCorrect ||
                !observation.IsPartiallyOffscreenWindowCaptureCorrect ||
                !observation.IsFullyOffscreenWindowCaptureUnavailableAsObserved ||
                !observation.IsMinimizedWindowCaptureCorrect ||
                !observation.IsBorderlessWindowCaptureCorrect ||
                !observation.IsScreenOrientationAndChannelOrderCorrect ||
                !observation.IsExplicitWindowListScreenCaptureCorrect)
            {
                throw new ProbeValidationException("One or more macOS capture invariants did not match the IVisualElementCapture contract.", observation);
            }

            return observation;
        }
        finally
        {
            StopProvider(provider);
            WaitForDiagnosticOutput(errorOutputTask);
        }
    }

    private static CaptureProviderObservation DeserializeCaptureProviderObservation(string json, string scenario) =>
        JsonSerializer.Deserialize<CaptureProviderObservation>(json) ??
        throw new InvalidOperationException($"The capture provider returned an invalid {scenario} observation.");

    private static int FindWindowIndex(IReadOnlyList<CGWindowZOrder.Entry> windows, uint windowId)
    {
        for (var index = 0; index < windows.Count; index++)
        {
            if (windows[index].WindowId == windowId)
            {
                return index;
            }
        }

        return -1;
    }

    private static OffscreenWindowCaptureObservation ObserveOffscreenCapture(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        VisualElementQueryResult retainedWindow,
        VisualElementQueryRequest request,
        CaptureProviderObservation providerObservation,
        double expectedScale)
    {
        var retainedElementCapture = TryObserveCapture(
            () => retainedWindow.Element.CaptureAsync().GetAwaiter().GetResult(),
            providerObservation.Points,
            expectedScale);
        var isNativeWindowReacquired = false;
        var reacquiredElementCapture = TryObserveCapture(
            () =>
            {
                using var freshContext = new VisualContext();
                using var freshRetention = freshContext.CreateRetention();
                var freshWindow = backend.Query(
                    freshRetention,
                    VisualElementLocator.FromNativeWindow((nint)providerObservation.WindowId),
                    VisualElementResolution.Direct,
                    request) ?? throw new InvalidOperationException("The offscreen window could not be reacquired from its Quartz ID.");
                Require(freshWindow.IsSuccess, "The reacquired offscreen-window query failed.");
                isNativeWindowReacquired = true;
                return freshWindow.Element.CaptureAsync().GetAwaiter().GetResult();
            },
            providerObservation.Points,
            expectedScale);
        var screen = backend.Query(
            retention,
            VisualElementLocator.FromNativeWindow((nint)providerObservation.WindowId),
            VisualElementResolution.Screen,
            request);
        return new OffscreenWindowCaptureObservation(providerObservation, screen?.IsSuccess == true, isNativeWindowReacquired, retainedElementCapture, reacquiredElementCapture);
    }

    private static IVisualElementCapture CaptureScreenWithExplicitWindowList(PixelRect bounds)
    {
        var rectangle = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
#pragma warning disable CA1422
        using var image = CGImage.ScreenImage(0, rectangle, CGWindowListOption.OnScreenOnly, CGWindowImageOption.Default);
#pragma warning restore CA1422
        return image is null ?
            throw new InvalidOperationException("The explicit CGWindowList screen capture returned null.") :
            new CapturedBitmapData(image, bounds);
    }

    private static CaptureAttempt TryObserveCapture(
        Func<IVisualElementCapture> capture,
        IReadOnlyList<CaptureProviderPoint> points,
        double? expectedScale)
    {
        try
        {
            using var result = capture();
            return new CaptureAttempt(ObserveCapture(result, points, expectedScale), null);
        }
        catch (Exception exception)
        {
            return new CaptureAttempt(null, exception.ToString());
        }
    }

    private static CaptureObservation ObserveCapture(
        IVisualElementCapture capture,
        IReadOnlyList<CaptureProviderPoint> points,
        double? expectedScale)
    {
        if (capture.Size.Width <= 0 || capture.Size.Height <= 0 || capture.Bounds.Width <= 0 || capture.Bounds.Height <= 0)
        {
            throw new InvalidOperationException("The capture returned an empty bitmap or desktop region.");
        }

        var samples = points.Select(point => SampleCapture(capture, point)).ToArray();
        var scaleX = (double)capture.Size.Width / capture.Bounds.Width;
        var scaleY = (double)capture.Size.Height / capture.Bounds.Height;
        var doesDensityMatchExpected = expectedScale is null ||
            Math.Abs(scaleX - expectedScale.Value) <= 0.05 && Math.Abs(scaleY - expectedScale.Value) <= 0.05;
        return new CaptureObservation(
            capture.Bounds,
            capture.Size,
            capture.Stride,
            scaleX,
            scaleY,
            expectedScale,
            doesDensityMatchExpected,
            samples.All(static sample => sample.DoesMatchExpected),
            samples);
    }

    private static CaptureSampleObservation SampleCapture(IVisualElementCapture capture, CaptureProviderPoint point)
    {
        var normalizedX = (point.X - capture.Bounds.X) / capture.Bounds.Width;
        var normalizedY = (point.Y - capture.Bounds.Y) / capture.Bounds.Height;
        var pixelX = Math.Clamp((int)(normalizedX * capture.Size.Width), 0, capture.Size.Width - 1);
        var pixelY = Math.Clamp((int)(normalizedY * capture.Size.Height), 0, capture.Size.Height - 1);
        var red = 0;
        var green = 0;
        var blue = 0;
        var alpha = 0;
        var sampleCount = 0;
        for (var y = Math.Max(0, pixelY - 2); y <= Math.Min(capture.Size.Height - 1, pixelY + 2); y++)
        {
            for (var x = Math.Max(0, pixelX - 2); x <= Math.Min(capture.Size.Width - 1, pixelX + 2); x++)
            {
                var offset = checked(y * capture.Stride + x * 4);
                red += Marshal.ReadByte(capture.Data, offset);
                green += Marshal.ReadByte(capture.Data, offset + 1);
                blue += Marshal.ReadByte(capture.Data, offset + 2);
                alpha += Marshal.ReadByte(capture.Data, offset + 3);
                sampleCount++;
            }
        }

        var color = new CaptureColor(red / sampleCount, green / sampleCount, blue / sampleCount, alpha / sampleCount);
        return new CaptureSampleObservation(
            point.Name,
            point.X,
            point.Y,
            pixelX,
            pixelY,
            color,
            DoesColorMatch(point.Name, color));
    }

    private static bool DoesColorMatch(string name, CaptureColor color) => name switch
    {
        "capture-top-left" => color is { Red: >= 180, Green: <= 100, Blue: <= 100, Alpha: >= 240 },
        "capture-top-right" => color is { Red: <= 100, Green: >= 120, Blue: <= 100, Alpha: >= 240 },
        "capture-bottom-left" => color is { Red: <= 100, Green: <= 100, Blue: >= 180, Alpha: >= 240 },
        "capture-bottom-right" => color is { Red: >= 180, Green: >= 180, Blue: <= 100, Alpha: >= 240 },
        _ => false,
    };

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "QueryElementAtPointForProcess")]
    private static extern VisualElementQueryResult? QueryElementAtPointForProcess(
        MacVisualElementBackend backend,
        VisualElementRetention retention,
        int processId,
        PixelPoint point,
        VisualElementResolution resolution,
        VisualElementQueryRequest request);

    [LibraryImport(CoreGraphicsFramework)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CGPreflightScreenCaptureAccess();

    private sealed record CaptureProviderObservation(
        uint WindowId,
        double BackingScaleFactor,
        bool IsBorderless,
        bool IsMinimized,
        string Placement,
        double VisibleAreaRatio,
        bool IsOccluded,
        uint? OccluderWindowId,
        IReadOnlyList<CaptureProviderPoint> Points);

    private sealed record CaptureProviderPoint(string Name, double X, double Y);

    private sealed record CaptureColor(int Red, int Green, int Blue, int Alpha);

    private sealed record CaptureSampleObservation(string Name, double DesktopX, double DesktopY, int PixelX, int PixelY, CaptureColor Color, bool DoesMatchExpected);

    private sealed record CaptureObservation(PixelRect Bounds, PixelSize Size, int Stride, double ScaleX, double ScaleY, double? ExpectedScale, bool DoesDensityMatchExpected, bool AreExpectedColorsObserved, IReadOnlyList<CaptureSampleObservation> Samples);

    private sealed record CaptureAttempt(CaptureObservation? Observation, string? Error);

    private sealed record OccludedWindowCaptureObservation(
        CaptureProviderObservation Provider,
        int TargetZOrderIndex,
        int OccluderZOrderIndex,
        bool IsOccluderAheadInZOrder,
        CaptureAttempt WindowCapture);

    private sealed record OffscreenWindowCaptureObservation(
        CaptureProviderObservation Provider,
        bool IsScreenResolved,
        bool IsNativeWindowReacquired,
        CaptureAttempt RetainedElementCapture,
        CaptureAttempt ReacquiredElementCapture);

    private sealed record CaptureContractObservation(
        int ProviderProcessId,
        uint NativeWindowId,
        double BackingScaleFactor,
        PixelRect WindowBounds,
        PixelRect ChildBounds,
        PixelFormat PixelFormat,
        AlphaFormat AlphaFormat,
        CaptureObservation WindowCapture,
        CaptureObservation ChildCapture,
        OccludedWindowCaptureObservation OccludedWindowCapture,
        OffscreenWindowCaptureObservation PartiallyOffscreenWindowCapture,
        OffscreenWindowCaptureObservation FullyOffscreenWindowCapture,
        CaptureAttempt MinimizedWindowCapture,
        CaptureAttempt BorderlessWindowCapture,
        bool IsScreenCaptureAuthorized,
        CaptureAttempt ScreenCapture,
        CaptureAttempt ExplicitWindowListScreenCapture,
        bool IsPixelFormatDeclaredCorrectly,
        bool DoesWindowSurfaceMatchAxBounds,
        bool IsWindowOrientationAndChannelOrderCorrect,
        bool IsChildCropCorrect,
        bool IsOccludedWindowCaptureCorrect,
        bool IsPartiallyOffscreenWindowCaptureCorrect,
        bool IsFullyOffscreenWindowCaptureUnavailableAsObserved,
        bool IsMinimizedWindowCaptureCorrect,
        bool IsBorderlessWindowCaptureCorrect,
        bool IsScreenOrientationAndChannelOrderCorrect,
        bool IsExplicitWindowListScreenCaptureCorrect);
}

using Avalonia.Platform;
using Everywhere.Automation;

namespace Everywhere.ProcessIsolation.Automation;

internal static class AutomationHostRpcModelExtensions
{
    public static AcquireAutomationAnchorRequest ToAcquireRequest(
        this VisualElementLocator locator,
        long contextId,
        long anchorId,
        VisualElementResolution resolution,
        VisualElementQueryRequest query) =>
        new()
        {
            ContextId = contextId,
            AnchorId = anchorId,
            LocatorKind = locator.Kind,
            PointX = locator.Point.X,
            PointY = locator.Point.Y,
            NativeWindowHandle = locator.NativeWindowHandle,
            Resolution = resolution,
            RequestedFields = query.RequestedFields,
            MaxTextCharacters = query.MaxTextCharacters,
        };

    public static VisualElementLocator ToLocator(this AcquireAutomationAnchorRequest request) => request.LocatorKind switch
    {
        VisualElementLocatorKind.Default => VisualElementLocator.Default,
        VisualElementLocatorKind.Focused => VisualElementLocator.Focused,
        VisualElementLocatorKind.Pointer => VisualElementLocator.Pointer,
        VisualElementLocatorKind.Point => VisualElementLocator.FromPoint(new PixelPoint(request.PointX, request.PointY)),
        VisualElementLocatorKind.NativeWindow => VisualElementLocator.FromNativeWindow((nint)request.NativeWindowHandle),
        _ => throw new ArgumentOutOfRangeException(nameof(request.LocatorKind), request.LocatorKind, null),
    };

    public static AcquireAutomationAnchorResponse ToResponse(this VisualElementQueryResult? result)
    {
        if (result is null)
        {
            return new AcquireAutomationAnchorResponse
            {
                IsAvailable = false,
                ElementId = null,
                Type = null,
                States = null,
                Name = null,
                TextPreview = null,
                HasMoreText = false,
                HasBounds = false,
                BoundsX = 0,
                BoundsY = 0,
                BoundsWidth = 0,
                BoundsHeight = 0,
                ProcessId = null,
                NativeWindowHandle = null,
                AvailableFields = VisualElementFields.None,
                MissingFields = VisualElementFields.None,
                FailureKind = null,
            };
        }

        var snapshot = result.Snapshot;
        var bounds = snapshot.Bounds.GetValueOrDefault();
        return new AcquireAutomationAnchorResponse
        {
            IsAvailable = true,
            ElementId = snapshot.Id,
            Type = snapshot.Type,
            States = snapshot.States,
            Name = snapshot.Name,
            TextPreview = snapshot.TextPreview,
            HasMoreText = snapshot.HasMoreText,
            HasBounds = snapshot.Bounds.HasValue,
            BoundsX = bounds.X,
            BoundsY = bounds.Y,
            BoundsWidth = bounds.Width,
            BoundsHeight = bounds.Height,
            ProcessId = snapshot.ProcessId,
            NativeWindowHandle = snapshot.NativeWindowHandle,
            AvailableFields = result.AvailableFields,
            MissingFields = result.MissingFields,
            FailureKind = result.Failure?.Kind,
        };
    }

    public static AcquireAutomationAnchorResponse ToUnavailableResponse(this VisualElementQueryFailureKind failureKind) => new()
    {
        IsAvailable = false,
        ElementId = null,
        Type = null,
        States = null,
        Name = null,
        TextPreview = null,
        HasMoreText = false,
        HasBounds = false,
        BoundsX = 0,
        BoundsY = 0,
        BoundsWidth = 0,
        BoundsHeight = 0,
        ProcessId = null,
        NativeWindowHandle = null,
        AvailableFields = VisualElementFields.None,
        MissingFields = VisualElementFields.None,
        FailureKind = failureKind,
    };

    public static AcquireAutomationAnchorResponse ToResponse(this VisualContextSnapshotNode node)
    {
        var snapshot = node.Snapshot;
        var bounds = snapshot.Bounds.GetValueOrDefault();
        return new AcquireAutomationAnchorResponse
        {
            IsAvailable = true,
            ElementId = snapshot.Id,
            Type = snapshot.Type,
            States = snapshot.States,
            Name = snapshot.Name,
            TextPreview = snapshot.TextPreview,
            HasMoreText = snapshot.HasMoreText,
            HasBounds = snapshot.Bounds.HasValue,
            BoundsX = bounds.X,
            BoundsY = bounds.Y,
            BoundsWidth = bounds.Width,
            BoundsHeight = bounds.Height,
            ProcessId = snapshot.ProcessId,
            NativeWindowHandle = snapshot.NativeWindowHandle,
            AvailableFields = node.AvailableFields,
            MissingFields = node.MissingFields,
            FailureKind = null,
        };
    }

    public static VisualElementSnapshot ToSnapshot(this AcquireAutomationAnchorResponse response) =>
        new(
            response.ElementId,
            response.Type,
            response.States,
            response.Name,
            response.TextPreview,
            response.HasMoreText,
            response.HasBounds ? new PixelRect(response.BoundsX, response.BoundsY, response.BoundsWidth, response.BoundsHeight) : null,
            response.ProcessId,
            response.NativeWindowHandle is { } handle ? (nint)handle : null);

    public static AutomationCaptureHeader ToHeader(this IVisualElementCapture capture) =>
        new()
        {
            BoundsX = capture.Bounds.X,
            BoundsY = capture.Bounds.Y,
            BoundsWidth = capture.Bounds.Width,
            BoundsHeight = capture.Bounds.Height,
            PixelWidth = capture.Size.Width,
            PixelHeight = capture.Size.Height,
            Stride = capture.Stride,
            PixelFormat = capture.Format.ToAutomationFormat(),
            AlphaFormat = capture.AlphaFormat.ToAutomationFormat(),
            DataLength = checked(capture.Stride * capture.Size.Height),
        };

    public static PixelFormat ToPixelFormat(this AutomationCapturePixelFormat format) => format switch
    {
        AutomationCapturePixelFormat.Bgra8888 => PixelFormat.Bgra8888,
        AutomationCapturePixelFormat.Rgba8888 => PixelFormat.Rgba8888,
        AutomationCapturePixelFormat.Rgb565 => PixelFormat.Rgb565,
        AutomationCapturePixelFormat.Rgb32 => PixelFormat.Rgb32,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    public static AlphaFormat ToAlphaFormat(this AutomationCaptureAlphaFormat format) => format switch
    {
        AutomationCaptureAlphaFormat.Opaque => AlphaFormat.Opaque,
        AutomationCaptureAlphaFormat.Premultiplied => AlphaFormat.Premul,
        AutomationCaptureAlphaFormat.Unpremultiplied => AlphaFormat.Unpremul,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    private static AutomationCapturePixelFormat ToAutomationFormat(this PixelFormat format)
    {
        if (format == PixelFormat.Bgra8888) return AutomationCapturePixelFormat.Bgra8888;
        if (format == PixelFormat.Rgba8888) return AutomationCapturePixelFormat.Rgba8888;
        if (format == PixelFormat.Rgb565) return AutomationCapturePixelFormat.Rgb565;
        if (format == PixelFormat.Rgb32) return AutomationCapturePixelFormat.Rgb32;
        throw new NotSupportedException($"The capture pixel format '{format}' is not supported by Automation RPC.");
    }

    private static AutomationCaptureAlphaFormat ToAutomationFormat(this AlphaFormat format) => format switch
    {
        AlphaFormat.Opaque => AutomationCaptureAlphaFormat.Opaque,
        AlphaFormat.Premul => AutomationCaptureAlphaFormat.Premultiplied,
        AlphaFormat.Unpremul => AutomationCaptureAlphaFormat.Unpremultiplied,
        _ => throw new NotSupportedException($"The capture alpha format '{format}' is not supported by Automation RPC."),
    };
}
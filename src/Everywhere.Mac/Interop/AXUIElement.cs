using System.Runtime.InteropServices;
using Avalonia;
using CoreFoundation;
using Everywhere.Automation;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides interop methods for macOS Accessibility API (AXUIElement).
/// </summary>
public partial class AXUIElement : NSObject
{
    internal nint NativeHandle => Handle.Handle;

    /// <summary>
    /// Create AXUIElement from NSArray at given index.
    /// </summary>
    /// <param name="array"></param>
    /// <param name="index"></param>
    /// <returns></returns>
    private static AXUIElement? FromCopyArray(NSArray array, nuint index)
    {
        var pValue = array.ValueAt(index);
        if (pValue.Handle == 0) return null;

        CFInterop.CFRetain(pValue);
        return new AXUIElement(pValue.Handle);
    }

    /// <summary>
    /// Retains one borrowed AX element stored in an array and returns an independent managed owner.
    /// </summary>
    internal static AXUIElement? FromArray(NSArray array, nuint index) => FromCopyArray(array, index);

    /// <summary>
    /// Creates another managed owner for the same native AX element.
    /// </summary>
    internal AXUIElement Retain()
    {
        CFInterop.CFRetain(Handle);
        return new AXUIElement(Handle);
    }

    private AXUIElement(NativeHandle handle) : base(handle, true) { }

    public void SetText(string text)
    {
        using var nsText = new NSString(text);
        var error = SetAttributeValue(Handle, AXAttributeConstants.Value.Handle, nsText.Handle);
        if (error != AXError.Success)
        {
            throw new AXException(error, $"Failed to set the AX value. AX returned {error}.");
        }
    }

    /// <summary>
    /// Get the selected text of the visual element.
    /// In case of numeric input fields that return NSNumber, it will be converted to string.
    /// </summary>
    /// <returns></returns>
    public string? GetSelectionText()
    {
        var error = CopyDescriptionAttribute(AXAttributeConstants.SelectedText, out var text);
        return error switch
        {
            AXError.Success => text,
            AXError.AttributeUnsupported or AXError.NoValue or AXError.NotImplemented => null,
            _ => throw new AXException(error, $"Failed to copy the AX selected text. AX returned {error}."),
        };
    }

    public Task<IVisualElementCapture> CaptureAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = GetBounds();
        if (bounds.Width <= 0 || bounds.Height <= 0) return Task.FromResult<IVisualElementCapture>(CapturedBitmapData.Empty);

        var roleError = CopyStringAttribute(AXAttributeConstants.Role, out var role);
        ThrowIfRequiredAttributeFailed(roleError, "read the AX role before capture");
        var isWindow = roleError == AXError.Success && role == nameof(AXRoleAttribute.AXWindow);
        var windowError = AXError.Success;
        using var windowRef = isWindow ? null : GetAttributeAsElement(AXAttributeConstants.Window, out windowError);
        ThrowIfRequiredAttributeFailed(windowError, "locate the captured element's AX window");
        var windowBounds = isWindow ?
            bounds :
            windowRef?.GetBounds() ?? throw new InvalidOperationException("Cannot locate the captured window's desktop region.");
        if (windowBounds.Width <= 0 || windowBounds.Height <= 0) throw new InvalidOperationException("The captured window has no drawable region.");

        // Retain the hardware path: unlike CGWindowListCreateImage, the native probe verifies that it
        // captures minimized, fully occluded, and partially off-desktop windows without clipping the
        // returned surface to the visible display area. On macOS 15.7.3 it returns no image once the
        // whole window is outside every display, even when its AX element and Quartz ID remain valid.
        // Do not reposition another application's window as an implicit capture fallback.
        // FullSize preserves the existing Stage Manager workaround.
        // Keep best resolution for the general capture API. An animation-only producer may request
        // NominalResolution later; neither option changes Bounds or Avalonia's desktop coordinates.
        // NativeWindowHandle belongs only to a real AXWindow. Descendants, including cross-process
        // WebKit nodes, reach their enclosing window through AXWindow for capture without publishing
        // the enclosing Quartz identifier as if it were the descendant's own scalar field.
        var nativeWindowError = (windowRef ?? this).GetNativeWindowHandle(out var nativeWindowHandle);
        if (nativeWindowError != AXError.Success || nativeWindowHandle == 0)
        {
            return Task.FromException<IVisualElementCapture>(
                new AXException(nativeWindowError, $"Failed to map the AX element to a Quartz window. AX returned {nativeWindowError}."));
        }

        using var cgImage = SkyLightInterop.HardwareCaptureWindowList(
            [nativeWindowHandle],
            SkyLightInterop.CGSWindowCaptureOptions.IgnoreGlobalCLipShape |
            SkyLightInterop.CGSWindowCaptureOptions.BestResolution |
            SkyLightInterop.CGSWindowCaptureOptions.FullSize);
        if (cgImage is null) throw new InvalidOperationException("Failed to capture screen image.");
        cancellationToken.ThrowIfCancellationRequested();

        var imageWidth = checked((int)cgImage.Width);
        var imageHeight = checked((int)cgImage.Height);
        if (imageWidth <= 0 || imageHeight <= 0) throw new InvalidOperationException("The captured window image is empty.");

        // Derive surface density from the returned image, not the first intersecting NSScreen. A native
        // AppKit probe verifies exact AX-bound mapping for ordinary shadowed, borderless, minimized, and
        // active-Stage-Manager windows. This does not characterize an inactive Stage or separate Space;
        // if either adds framing, obtain that surface's actual origin/extent rather than adding tolerances.
        var scaleX = (double)imageWidth / windowBounds.Width;
        var scaleY = (double)imageHeight / windowBounds.Height;
        var left = (int)Math.Clamp(Math.Floor(((double)bounds.X - windowBounds.X) * scaleX), 0, imageWidth);
        var top = (int)Math.Clamp(Math.Floor(((double)bounds.Y - windowBounds.Y) * scaleY), 0, imageHeight);
        var right = (int)Math.Clamp(Math.Ceiling(((double)bounds.Right - windowBounds.X) * scaleX), 0, imageWidth);
        var bottom = (int)Math.Clamp(Math.Ceiling(((double)bounds.Bottom - windowBounds.Y) * scaleY), 0, imageHeight);
        if (right <= left || bottom <= top) throw new InvalidOperationException("The requested region does not intersect the captured surface.");

        // CGImage cropping and the copied buffer both preserve the top-left desktop orientation; the
        // asymmetric native probe verifies a top quadrant independently from the full-window image.
        using var croppedImage = cgImage.WithImageInRect(new CGRect(left, top, right - left, bottom - top));
        if (croppedImage is null) throw new InvalidOperationException("Failed to crop image.");

        // Report the pixel-aligned crop's coverage, not the original request. PixelRect preserves
        // the existing integer desktop API; outward rounding can add less than one point per edge.
        var desktopLeft = checked((int)Math.Floor(windowBounds.X + left / scaleX));
        var desktopTop = checked((int)Math.Floor(windowBounds.Y + top / scaleY));
        var desktopRight = checked((int)Math.Ceiling(windowBounds.X + right / scaleX));
        var desktopBottom = checked((int)Math.Ceiling(windowBounds.Y + bottom / scaleY));
        var capturedBounds = new PixelRect(desktopLeft, desktopTop, checked(desktopRight - desktopLeft), checked(desktopBottom - desktopTop));
        return Task.FromResult<IVisualElementCapture>(new CapturedBitmapData(croppedImage, capturedBounds));
    }

    private static void ThrowIfRequiredAttributeFailed(AXError error, string operation)
    {
        if (error is AXError.Success or AXError.AttributeUnsupported or AXError.NoValue or AXError.NotImplemented) return;
        throw new AXException(error, $"Failed to {operation}. AX returned {error}.");
    }

    public bool SetAttribute(NSString attributeName, NSObject value)
    {
        var error = SetAttributeValue(Handle, attributeName.Handle, value.Handle);
        return error == AXError.Success;
    }

    public override bool Equals(object? obj)
    {
        return obj is AXUIElement element && CFType.Equal(Handle, element.Handle);
    }

    public override int GetHashCode()
    {
        return CFInterop.CFHash(Handle).GetHashCode();
    }

    #region Helpers

    private PixelRect GetBounds()
    {
        using var batch = AXAttributeBatch.Copy(this, [AXAttributeConstants.Position, AXAttributeConstants.Size]);
        if (batch.Error != AXError.Success ||
            batch.GetPoint(AXAttributeConstants.Position, out var position) != AXError.Success ||
            batch.GetSize(AXAttributeConstants.Size, out var size) != AXError.Success ||
            position is not { } point ||
            size is not { } dimensions)
        {
            return default;
        }

        return new PixelRect((int)point.X, (int)point.Y, (int)dimensions.Width, (int)dimensions.Height);
    }

    internal AXUIElement? GetAttributeAsElement(NSString attributeName, out AXError error)
    {
        error = CopyAttributeValue(Handle, attributeName.Handle, out var value);
        if (error == AXError.Success && value != 0)
        {
            return new AXUIElement(value);
        }

        if (value != 0) CFInterop.CFRelease(value);
        return null;
    }

    internal AXError CopyMultipleAttributeValues(NSArray attributes, out NSArray? values)
    {
        var error = CopyMultipleAttributeValues(Handle, attributes.Handle, 0, out var valuesHandle);
        if (error != AXError.Success)
        {
            if (valuesHandle != 0) CFInterop.CFRelease(valuesHandle);
            values = null;
            return error;
        }

        values = valuesHandle != 0 ? Runtime.GetNSObject<NSArray>(valuesHandle, owns: true) : null;
        return error;
    }

    internal AXError GetAttributeValueCount(NSString attributeName, out nint count) =>
        GetAttributeValueCount(Handle, attributeName.Handle, out count);

    internal AXError CopyAttributeValues(NSString attributeName, nint index, nint maximumValues, out NSArray? values)
    {
        var error = CopyAttributeValues(Handle, attributeName.Handle, index, maximumValues, out var valuesHandle);
        if (error != AXError.Success)
        {
            if (valuesHandle != 0) CFInterop.CFRelease(valuesHandle);
            values = null;
            return error;
        }

        values = valuesHandle != 0 ? Runtime.GetNSObject<NSArray>(valuesHandle, owns: true) : null;
        return error;
    }

    internal AXError CopyStringAttribute(NSString attributeName, out string? value)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var valueHandle);
        try
        {
            value = error == AXError.Success && AXScalarValueReader.TryReadString(valueHandle, out var result) ? result : null;
            return error == AXError.Success && value is null ? AXError.Failure : error;
        }
        finally
        {
            if (valueHandle != 0) CFInterop.CFRelease(valueHandle);
        }
    }

    internal AXError CopyBooleanAttribute(NSString attributeName, out bool? value)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var valueHandle);
        try
        {
            value = error == AXError.Success && AXScalarValueReader.TryReadBoolean(valueHandle, out var result) ? result : null;
            return error == AXError.Success && value is null ? AXError.Failure : error;
        }
        finally
        {
            if (valueHandle != 0) CFInterop.CFRelease(valueHandle);
        }
    }

    internal AXError CopyDescriptionAttribute(NSString attributeName, out string? value)
    {
        var error = CopyAttributeValue(Handle, attributeName.Handle, out var valueHandle);
        try
        {
            value = error == AXError.Success ? AXScalarValueReader.ReadDescription(valueHandle) : null;
            return error == AXError.Success && value is null ? AXError.Failure : error;
        }
        finally
        {
            if (valueHandle != 0) CFInterop.CFRelease(valueHandle);
        }
    }

    internal AXError CopyParameterizedInt64Attribute(NSString attributeName, AXUIElement parameter, out long? value)
    {
        var error = CopyParameterizedAttributeValue(Handle, attributeName.Handle, parameter.NativeHandle, out var valueHandle);
        try
        {
            value = error == AXError.Success && AXScalarValueReader.TryReadInt64(valueHandle, out var result) ? result : null;
            return error == AXError.Success && value is null ? AXError.Failure : error;
        }
        finally
        {
            if (valueHandle != 0) CFInterop.CFRelease(valueHandle);
        }
    }

    internal unsafe AXError CopyParameterizedStringAttribute(NSString attributeName, nint location, nint length, out string? value)
    {
        value = null;
        var range = new NativeCFRange(location, length);
        var parameter = AXValueCreate(AXValueType.CFRange, (nint)(&range));
        if (parameter == 0) return AXError.Failure;

        try
        {
            var error = CopyParameterizedAttributeValue(Handle, attributeName.Handle, parameter, out var valueHandle);
            try
            {
                value = error == AXError.Success && AXScalarValueReader.TryReadString(valueHandle, out var result) ? result : null;
                return error == AXError.Success && value is null ? AXError.Failure : error;
            }
            finally
            {
                if (valueHandle != 0) CFInterop.CFRelease(valueHandle);
            }
        }
        finally
        {
            CFInterop.CFRelease(parameter);
        }
    }

    internal void PerformAction(NSString actionName)
    {
        var error = PerformAction(Handle, actionName.Handle);
        if (error != AXError.Success)
        {
            throw new AXException(error, $"Failed to perform the AX action {actionName}. AX returned {error}.");
        }
    }

    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    public static AXUIElement CreateSystemWideElement()
    {
        var handle = CreateSystemWide();
        return handle != 0 ?
            new AXUIElement(handle) :
            throw new InvalidOperationException("Could not create the macOS system-wide Accessibility element.");
    }

    public AXError SetMessagingTimeout(TimeSpan timeout) => AXUIElementSetMessagingTimeout(Handle, (float)timeout.TotalSeconds);

    public AXError GetProcessId(out int processId) => GetPid(Handle, out processId);

    public AXError GetNativeWindowHandle(out uint nativeWindowHandle) => GetWindow(Handle, out nativeWindowHandle);

    public AXError SetAttributeValueWithError(NSString attributeName, NSObject value) =>
        SetAttributeValue(Handle, attributeName.Handle, value.Handle);

    public AXUIElement? ElementAtPosition(float x, float y, out AXError error)
    {
        error = CopyElementAtPosition(Handle, x, y, out var element);
        if (error == AXError.Success && element != 0)
        {
            return new AXUIElement(element);
        }

        if (element != 0) CFInterop.CFRelease(element);
        return null;
    }

    public static AXUIElement? ElementFromPid(int pid)
    {
        var handle = CreateApplication(pid);
        return handle != 0 ? new AXUIElement(handle) : null;
    }

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateSystemWide")]
    private static partial nint CreateSystemWide();

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetMessagingTimeout")]
    private static partial AXError AXUIElementSetMessagingTimeout(nint element, float timeoutInSeconds);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyElementAtPosition")]
    private static partial AXError CopyElementAtPosition(nint application, float x, float y, out nint element);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyAttributeValue")]
    private static partial AXError CopyAttributeValue(nint element, nint attribute, out nint value);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyMultipleAttributeValues")]
    private static partial AXError CopyMultipleAttributeValues(nint element, nint attributes, uint options, out nint values);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementGetAttributeValueCount")]
    private static partial AXError GetAttributeValueCount(nint element, nint attribute, out nint count);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyAttributeValues")]
    private static partial AXError CopyAttributeValues(nint element, nint attribute, nint index, nint maximumValues, out nint values);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCopyParameterizedAttributeValue")]
    private static partial AXError CopyParameterizedAttributeValue(nint element, nint parameterizedAttribute, nint parameter, out nint value);

    [LibraryImport(AppServices, EntryPoint = "AXValueCreate")]
    private static partial nint AXValueCreate(AXValueType valueType, nint value);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementPerformAction")]
    private static partial AXError PerformAction(nint element, nint action);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementSetAttributeValue")]
    private static partial AXError SetAttributeValue(nint element, nint attribute, nint value);

    /// <summary>
    /// Private API from https://github.com/lwouis/alt-tab-macos/blob/9761bb91e97646f1c30b43842c4694615e9ad39b/src/api-wrappers/private-apis/ApplicationServices.HIServices.framework.swift#L5
    /// </summary>
    [LibraryImport(AppServices, EntryPoint = "_AXUIElementGetWindow")]
    private static partial AXError GetWindow(nint element, out uint cgWindowId);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementGetPid")]
    private static partial AXError GetPid(nint element, out int pid);

    [LibraryImport(AppServices, EntryPoint = "AXUIElementCreateApplication")]
    private static partial nint CreateApplication(int pid);

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeCFRange(nint Location, nint Length);

    #endregion
}
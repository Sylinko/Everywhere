using System.Globalization;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Owns the process-shared macOS Accessibility entry point and acquires elements in caller-selected visual contexts.
/// </summary>
public sealed class MacVisualElementBackend : IVisualElementBackend, IDisposable
{
    private const int MaximumTopLevelAncestorDepth = 256;

    private AXUIElement SystemWide => _systemWide ?? throw new ObjectDisposedException(nameof(MacVisualElementBackend));

    private AXUIElement? _systemWide;
    private long _nextVisualElementId;

    /// <summary>
    /// Initializes the macOS Accessibility backend with the established one-second messaging timeout.
    /// </summary>
    public MacVisualElementBackend() : this(new VisualContextPlatformOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)))
    {
    }

    /// <summary>
    /// Initializes the macOS Accessibility backend with one immutable messaging-timeout policy.
    /// </summary>
    public MacVisualElementBackend(VisualContextPlatformOptions options)
    {
        options.Validate();
        var systemWide = AXUIElement.CreateSystemWideElement();
        try
        {
            var error = systemWide.SetMessagingTimeout(options.TransactionTimeout);
            error.ThrowIfProviderFailure("configure the process-wide AX messaging timeout");
            _systemWide = systemWide;
        }
        catch
        {
            systemWide.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public VisualElementQueryResult? Query(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? request = null)
    {
        ObjectDisposedException.ThrowIf(retention.IsDisposed, retention);
        var effectiveRequest = request ?? VisualElementQueryRequest.Default;
        try
        {
            return resolution switch
            {
                VisualElementResolution.Direct => QueryDirect(retention, locator, effectiveRequest),
                VisualElementResolution.TopLevel => QueryTopLevel(retention, locator, effectiveRequest),
                VisualElementResolution.Screen => QueryScreen(retention, locator, effectiveRequest),
                _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
            };
        }
        catch (AXException exception)
        {
            throw exception.CreateException();
        }
    }

    /// <summary>
    /// Releases the process-shared system-wide Accessibility reference.
    /// </summary>
    public void Dispose()
    {
        _systemWide?.Dispose();
        _systemWide = null;
    }

    internal AXVisualElement GetOrCreateAXElement(VisualElementRetention retention, AXUIElement nativeElement)
    {
        var context = retention.Context;
        var identity = new AXUIElementIdentity(nativeElement.NativeHandle);
        return context.GetIdentityMap(AXUIElementIdentityComparer.Shared).GetOrAdd(
            retention,
            identity,
            (Backend: this, NativeElement: nativeElement, IsSystemWide: IsSystemWideElement(nativeElement)),
            static (elementIdentity, state) => AXVisualElement.Create(elementIdentity, state.Backend, state.NativeElement, state.IsSystemWide));
    }

    internal VisualElementQueryResult? QueryElementAtPointForProcess(
        VisualElementRetention retention,
        int processId,
        PixelPoint point,
        VisualElementResolution resolution,
        VisualElementQueryRequest request)
    {
        try
        {
            using var application = AXUIElement.ElementFromPid(processId);
            if (application is null)
            {
                return null;
            }

            using var source = application.ElementAtPosition(point.X, point.Y, out var pointError);
            pointError.ThrowIfProviderFailure("copy the process AX element at a point");
            if (source is null)
            {
                return null;
            }

            if (resolution == VisualElementResolution.Direct)
            {
                return GetOrCreateAXElement(retention, source).Query(request);
            }

            if (resolution != VisualElementResolution.TopLevel)
            {
                throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null);
            }

            using var topLevel = ResolveTopLevel(source);
            return topLevel is null ? null : GetOrCreateAXElement(retention, topLevel).Query(request);
        }
        catch (AXException exception)
        {
            throw exception.CreateException();
        }
    }

    internal string AllocateVisualElementId(string domain)
    {
        var id = Interlocked.Increment(ref _nextVisualElementId);
        if (id <= 0)
        {
            throw new OverflowException("The macOS visual-element identifier space was exhausted.");
        }

        return domain + ":" + id.ToString(CultureInfo.InvariantCulture);
    }

    private bool IsSystemWideElement(AXUIElement element) =>
        CFInterop.CFEqual(SystemWide.NativeHandle, element.NativeHandle);

    private VisualElementQueryResult? QueryDirect(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementQueryRequest request)
    {
        using var nativeElement = AcquireDirect(locator);
        return nativeElement is null ? null : GetOrCreateAXElement(retention, nativeElement).Query(request);
    }

    private VisualElementQueryResult? QueryTopLevel(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementQueryRequest request)
    {
        if (locator.Kind == VisualElementLocatorKind.Default)
        {
            return QueryFirstTopLevelWindow(retention, request);
        }

        using var source = AcquireDirect(locator);
        if (source is null)
        {
            return null;
        }

        using var topLevel = ResolveTopLevel(source);
        return topLevel is null ? null : GetOrCreateAXElement(retention, topLevel).Query(request);
    }

    private VisualElementQueryResult? QueryScreen(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementQueryRequest request)
    {
        var topology = CGDisplayTopology.Current;
        MacDisplay? display;
        switch (locator.Kind)
        {
            case VisualElementLocatorKind.Default:
                display = topology.Primary;
                break;
            case VisualElementLocatorKind.Pointer:
                display = topology.FindNearest(GetPointerPoint(topology));
                break;
            case VisualElementLocatorKind.Point:
                display = topology.FindNearest(locator.Point);
                break;
            default:
            {
                using var source = AcquireDirect(locator);
                using var topLevel = source is null ? null : ResolveTopLevel(source);
                display = topLevel is null ? null : FindWindowDisplay(topology, topLevel);
                break;
            }
        }

        return display is null ? null : GetOrCreateScreenElement(retention, topology, display).Query(request);
    }

    private AXUIElement? AcquireDirect(VisualElementLocator locator)
    {
        return locator.Kind switch
        {
            VisualElementLocatorKind.Default => SystemWide.Retain(),
            VisualElementLocatorKind.Focused => CopyFocusedElement(),
            VisualElementLocatorKind.Pointer => CopyElementAtPoint(GetPointerPoint(CGDisplayTopology.Current)),
            VisualElementLocatorKind.Point => CopyElementAtPoint(locator.Point),
            VisualElementLocatorKind.NativeWindow => ResolveNativeWindow(locator.NativeWindowHandle),
            _ => throw new ArgumentOutOfRangeException(nameof(locator), locator, null),
        };
    }

    private AXUIElement? CopyFocusedElement()
    {
        var element = SystemWide.GetAttributeAsElement(AXAttributeConstants.FocusedUIElement, out var error);
        error.ThrowIfProviderFailure("copy the focused AX element");
        return element;
    }

    private AXUIElement? CopyElementAtPoint(PixelPoint point)
    {
        var element = SystemWide.ElementAtPosition(point.X, point.Y, out var error);
        error.ThrowIfProviderFailure("copy the system-wide AX element at a point");
        return element;
    }

    private VisualElementQueryResult? QueryFirstTopLevelWindow(VisualElementRetention retention, VisualElementQueryRequest request)
    {
        var windowZOrder = CGWindowZOrder.Capture(CGDisplayTopology.Current);
        using var resolver = new AXWindowResolver(windowZOrder.Windows);
        foreach (var window in windowZOrder.Windows)
        {
            using var nativeWindow = resolver.Resolve(window);
            if (nativeWindow is not null)
            {
                return GetOrCreateAXElement(retention, nativeWindow).Query(request);
            }
        }

        return null;
    }

    private static AXUIElement? ResolveTopLevel(AXUIElement source)
    {
        var sourceRole = ReadRole(source, "read the source AX role while resolving a top-level element");
        if (sourceRole == AXRoleAttribute.AXWindow)
        {
            return source.Retain();
        }

        if (sourceRole is AXRoleAttribute.AXApplication or AXRoleAttribute.AXSystemWide)
        {
            return null;
        }

        // AXTopLevelUIElement may be a sheet or drawer. The projected root is the enclosing real AXWindow.
        var window = source.GetAttributeAsElement(AXAttributeConstants.Window, out var windowError);
        windowError.ThrowIfProviderFailure("copy the enclosing AXWindow");
        if (window is not null)
        {
            try
            {
                if (ReadRole(window, "validate the enclosing AXWindow role") == AXRoleAttribute.AXWindow)
                {
                    return window;
                }

                window.Dispose();
            }
            catch
            {
                window.Dispose();
                throw;
            }
        }

        var current = source.Retain();
        try
        {
            for (var depth = 0; depth < MaximumTopLevelAncestorDepth; depth++)
            {
                var role = ReadRole(current, "read an AX role while resolving a top-level element");
                if (role == AXRoleAttribute.AXWindow)
                {
                    return current;
                }

                if (role is AXRoleAttribute.AXApplication or AXRoleAttribute.AXSystemWide)
                {
                    current.Dispose();
                    return null;
                }

                var parent = current.GetAttributeAsElement(AXAttributeConstants.Parent, out var parentError);
                parentError.ThrowIfProviderFailure("copy an AX parent while resolving a top-level element");
                if (parent is null)
                {
                    current.Dispose();
                    return null;
                }

                current.Dispose();
                current = parent;
            }

            throw new InvalidOperationException($"AX top-level resolution exceeded the {MaximumTopLevelAncestorDepth}-ancestor limit.");
        }
        catch
        {
            current.Dispose();
            throw;
        }
    }

    private static AXUIElement? ResolveNativeWindow(nint nativeWindowHandle)
    {
        if (nativeWindowHandle <= 0 || (nuint)nativeWindowHandle > uint.MaxValue)
        {
            return null;
        }

        var windowId = (uint)nativeWindowHandle;
        if (!CGWindowZOrder.TryGetOwnerProcessId(windowId, out var processId))
        {
            return null;
        }

        var reference = new AXWindowResolver.AXWindowReference(windowId, processId);
        using var resolver = new AXWindowResolver([reference]);
        return resolver.Resolve(reference);
    }

    private static MacDisplay? FindWindowDisplay(CGDisplayTopology topology, AXUIElement window)
    {
        var error = window.GetNativeWindowHandle(out var windowId);
        error.ThrowIfProviderFailure("map the AXWindow to its Quartz window identifier");
        return error == AXError.Success && windowId != 0 ? CGWindowZOrder.Capture(topology).Find(windowId)?.Display : null;
    }

    private static AXRoleAttribute ReadRole(AXUIElement element, string operation)
    {
        var error = element.CopyStringAttribute(AXAttributeConstants.Role, out var nativeRole);
        error.ThrowIfProviderFailure(operation);
        return error == AXError.Success && Enum.TryParse<AXRoleAttribute>(nativeRole, true, out var role) ?
            role :
            AXRoleAttribute.AXUnknown;
    }

    private static PixelPoint GetPointerPoint(CGDisplayTopology topology)
    {
        var location = NSEvent.CurrentMouseLocation;
        var primaryDisplay = topology.Primary ?? throw new InvalidOperationException("macOS did not report a primary display.");
        return new PixelPoint((int)location.X, (int)(primaryDisplay.Bounds.Height - location.Y));
    }

    public ScreenVisualElement GetOrCreateScreenElement(VisualElementRetention retention, CGDisplayTopology topology, MacDisplay display)
    {
        return ScreenVisualElement.GetOrCreate(retention, this, topology, display);
    }
}
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Utilities;
using Everywhere.Windows.Interop;
using Everywhere.Windows.Interop.UIAutomation;

namespace Everywhere.Windows.Automation;

/// <summary>
/// Owns the process-shared Windows Automation services and acquires root elements in caller-selected visual contexts.
/// </summary>
public sealed class WindowsVisualElementBackend : IVisualElementBackend, IDisposable
{
    internal UIAutomationClient Automation => _automation ?? throw new ObjectDisposedException(nameof(WindowsVisualElementBackend));

    internal UIAutomationTreeWalker TreeWalker => _treeWalker ?? throw new ObjectDisposedException(nameof(WindowsVisualElementBackend));

    private static VisualElementQueryRequest NativeWindowResolutionRequest { get; } = new(VisualElementFields.NativeWindowHandle, 0);

    private UIAutomationClient? _automation;
    private UIAutomationTreeWalker? _treeWalker;

    /// <summary>
    /// Initializes the shared Windows UI Automation client with fixed provider timeouts.
    /// </summary>
    public WindowsVisualElementBackend()
    {
        var automation = UIAutomationClient.Create();
        try
        {
            automation.ConfigureTimeouts(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
            _automation = automation;
            _treeWalker = automation.CreateContentViewWalker();
        }
        catch
        {
            automation.Dispose();
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
        return resolution switch
        {
            VisualElementResolution.Direct => QueryDirect(retention, locator, effectiveRequest),
            VisualElementResolution.TopLevel => QueryTopLevel(retention, locator, effectiveRequest),
            VisualElementResolution.Screen => QueryScreen(retention, locator, effectiveRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(resolution), resolution, null),
        };
    }

    /// <summary>
    /// Releases the process-shared Windows UI Automation services.
    /// </summary>
    public void Dispose()
    {
        DisposeHelper.DisposeToDefault(ref _treeWalker);
        DisposeHelper.DisposeToDefault(ref _automation);
    }

    internal UIAutomationVisualElement GetOrCreateUIAutomationElement(VisualElementRetention retention, in UIAutomationElement cachedElement)
    {
        var context = retention.Context;
        var identityMap = context.GetIdentityMap(UIAutomationRuntimeIdComparer.Instance);
        var resolution = cachedElement.ReadCachedRuntimeId(
            (Map: identityMap, Retention: retention),
            static (runtimeId, state) =>
                state.Map.TryGetAlternate<ReadOnlySpan<int>, UIAutomationVisualElement>(state.Retention, runtimeId, out var element) ?
                    new UIAutomationIdentityResolution(element, null) :
                    new UIAutomationIdentityResolution(null, state.Map.CreateIdentity(runtimeId)));
        if (resolution.Element is { } existingElement)
        {
            return existingElement;
        }

        var identity = resolution.Identity ?? throw new InvalidOperationException("UI Automation returned an element without a cached RuntimeId.");
        var automationElement = cachedElement.Realize();
        UIAutomationVisualElement? candidate = null;
        try
        {
            candidate = new UIAutomationVisualElement(
                context,
                this,
                automationElement,
                identity.ToString(),
                cachedElement.CachedProcessId,
                cachedElement.CachedNativeWindowHandle,
                cachedElement.CachedControlType);
            return identityMap.GetOrAdd(retention, identity, candidate, static (_, element) => element);
        }
        finally
        {
            if (candidate is null)
            {
                automationElement.Dispose();
            }
        }
    }

    internal ScreenVisualElement GetOrCreateScreenElement(VisualElementRetention retention, WindowsDisplayTopology topology, WindowsDisplay display)
    {
        var context = retention.Context;
        return context.GetIdentityMap<ScreenIdentity>().GetOrAdd(
            retention,
            new ScreenIdentity(topology.Generation, display.MonitorHandle),
            (Context: context, Backend: this, TopologyGeneration: topology.Generation, Display: display),
            static (_, state) => new ScreenVisualElement(state.Context, state.Backend, state.TopologyGeneration, state.Display));
    }

    private VisualElementQueryResult?
        QueryDirect(VisualElementRetention retention, VisualElementLocator locator, VisualElementQueryRequest request) =>
        QueryAutomation(retention, locator, request);

    private VisualElementQueryResult? QueryAutomation(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementQueryRequest request)
    {
        try
        {
            using var cacheRequest = Automation.CreateElementCacheRequest(request.RequestedFields);
            using var cachedElement = locator.Kind switch
            {
                VisualElementLocatorKind.Default => Automation.GetRootElementBuildCache(cacheRequest),
                VisualElementLocatorKind.Focused => Automation.GetFocusedElementBuildCache(cacheRequest),
                VisualElementLocatorKind.Pointer => GetAutomationElementAtPointer(cacheRequest),
                VisualElementLocatorKind.Point => Automation.ElementFromPointBuildCache(locator.Point.X, locator.Point.Y, cacheRequest),
                VisualElementLocatorKind.NativeWindow => Automation.ElementFromHandleBuildCache(locator.NativeWindowHandle, cacheRequest),
                _ => throw new ArgumentOutOfRangeException(nameof(locator), locator, null),
            };
            if (!cachedElement.HasValue)
            {
                return null;
            }

            var element = GetOrCreateUIAutomationElement(retention, in cachedElement);
            return cachedElement.CreateQueryResult(element, request);
        }
        catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            throw WindowsUIAutomationFailure.CreateException(exception);
        }
    }

    private UIAutomationElement GetAutomationElementAtPointer(UIAutomationCacheRequest cacheRequest) =>
        PInvoke.GetCursorPos(out var point) ? Automation.ElementFromPointBuildCache(point.X, point.Y, cacheRequest) : default;

    private VisualElementQueryResult? QueryTopLevel(VisualElementRetention retention, VisualElementLocator locator, VisualElementQueryRequest request)
    {
        var windowHandle = ResolveTopLevelWindow(retention.Context, locator);
        return windowHandle == 0 ? null : QueryAutomation(retention, VisualElementLocator.FromNativeWindow(windowHandle), request);
    }

    private VisualElementQueryResult? QueryScreen(VisualElementRetention retention, VisualElementLocator locator, VisualElementQueryRequest request)
    {
        var topology = WindowsDisplayTopology.Current;
        WindowsDisplay? display;
        if (locator.Kind == VisualElementLocatorKind.Default)
        {
            display = topology.Primary;
        }
        else if (locator.Kind == VisualElementLocatorKind.Point)
        {
            display = topology.FindNearest(locator.Point);
        }
        else if (locator.Kind == VisualElementLocatorKind.Pointer)
        {
            display = PInvoke.GetCursorPos(out var point) ? topology.FindNearest(new PixelPoint(point.X, point.Y)) : null;
        }
        else
        {
            var windowHandle = ResolveTopLevelWindow(retention.Context, locator);
            display = windowHandle == 0 ? null : topology.FindTopLevelWindowDisplay((HWND)windowHandle);
        }

        return display is null ? null : GetOrCreateScreenElement(retention, topology, display).Query(request);
    }

    private nint ResolveTopLevelWindow(VisualContext context, VisualElementLocator locator)
    {
        if (locator.Kind == VisualElementLocatorKind.Default)
        {
            return FindFirstTopLevelWindow();
        }

        if (locator.Kind == VisualElementLocatorKind.NativeWindow)
        {
            return PInvoke.GetAncestor((HWND)locator.NativeWindowHandle, GET_ANCESTOR_FLAGS.GA_ROOTOWNER);
        }

        using var retention = context.CreateRetention();
        var source = QueryAutomation(retention, locator, NativeWindowResolutionRequest);
        return source?.Element is UIAutomationVisualElement element && element.TryGetRootOwnerWindow(out var windowHandle) ? windowHandle : 0;
    }

    private static nint FindFirstTopLevelWindow()
    {
        var topology = WindowsDisplayTopology.Current;
        var windowHandle = PInvoke.GetTopWindow(HWND.Null);
        while (windowHandle != HWND.Null)
        {
            if (topology.FindTopLevelWindowDisplay(windowHandle) is not null)
            {
                return windowHandle;
            }

            windowHandle = PInvoke.GetWindow(windowHandle, GET_WINDOW_CMD.GW_HWNDNEXT);
        }

        return 0;
    }

    private readonly record struct UIAutomationIdentityResolution(UIAutomationVisualElement? Element, UIAutomationRuntimeId? Identity);

    [Serializable]
    private readonly record struct ScreenIdentity(long TopologyGeneration, nint MonitorHandle);
}
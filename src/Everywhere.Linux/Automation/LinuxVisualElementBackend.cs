using Avalonia;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Linux.Automation;

/// <summary>
/// Composes compositor topology with scoped application accessibility without querying through Main's overlays.
/// </summary>
public sealed partial class LinuxVisualElementBackend(
    ICompositorStateProvider compositor,
    ISilentScreenCapturer screenCapturer) : IVisualElementBackend, IVisualPickerResolver
{
    /// <summary>
    /// Gets the selected desktop compositor provider.
    /// </summary>
    public ICompositorStateProvider Compositor { get; } = compositor;

    /// <summary>
    /// Gets the selected desktop output capturer.
    /// </summary>
    public ISilentScreenCapturer ScreenCapturer { get; } = screenCapturer;

    /// <summary>
    /// Gets the process-owned accessibility dispatcher.
    /// </summary>
    public AtspiClient Accessibility { get; } = new();

    private const int MaximumApplicationWindows = 128;
    private const int BoundsRetryDelayMilliseconds = 1000;
    private long _nextId;

    private readonly Dictionary<(int ProcessId, string WindowId), long> _boundsRetryTimes = [];

    /// <summary>
    /// Reports the short circuit for a window whose native bounds provider timed out.
    /// </summary>
    public bool IsBoundsQueryBackedOff(CompositorWindowInfo window)
    {
        var key = (window.ProcessId, window.Id);
        if (!_boundsRetryTimes.TryGetValue(key, out var retryAt))
        {
            return false;
        }

        if (Environment.TickCount64 < retryAt)
        {
            return true;
        }

        _boundsRetryTimes.Remove(key);
        return false;
    }

    /// <summary>
    /// Prevents one embedded provider from stalling every node in a tree read.
    /// </summary>
    public void RecordBoundsTimeout(CompositorWindowInfo window)
    {
        var now = Environment.TickCount64;
        foreach (var pair in _boundsRetryTimes.ToArray())
        {
            if (pair.Value <= now)
            {
                _boundsRetryTimes.Remove(pair.Key);
            }
        }

        _boundsRetryTimes[(window.ProcessId, window.Id)] = now + BoundsRetryDelayMilliseconds;
    }

    /// <summary>
    /// Reads the current output geometry and observed window order synchronously.
    /// </summary>
    public DesktopSnapshot ReadDesktop()
    {
        // Shared Automation contracts are synchronous. Providers perform bounded IPC
        // without capturing a synchronization context; no Avalonia work runs here.
        return new(Compositor.GetOutputsAsync().GetAwaiter().GetResult(), Compositor.GetWindowsInStackingOrderAsync().GetAwaiter().GetResult());
    }

    /// <summary>
    /// Allocates the next process-local visual-element identifier.
    /// </summary>
    public string AllocateId() => "linux:" + Interlocked.Increment(ref _nextId);

    /// <inheritdoc />
    public VisualElementQueryResult? Query(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? request = null)
    {
        return Acquire(retention, locator, resolution, request ?? VisualElementQueryRequest.Default, 0);
    }

    /// <inheritdoc />
    public VisualElementQueryResult? Resolve(
        IVisualElementBackend backend,
        VisualElementRetention retention,
        PixelPoint point,
        ScreenSelectionMode mode,
        int excludedProcessId,
        VisualElementQueryRequest request)
    {
        return Acquire(retention, VisualElementLocator.FromPoint(point), mode switch
            {
                ScreenSelectionMode.Screen => VisualElementResolution.Screen,
                ScreenSelectionMode.Window => VisualElementResolution.TopLevel,
                _ => VisualElementResolution.Direct,
            }, request, excludedProcessId);
    }

    /// <summary>
    /// Retains a compositor window in the requested visual context.
    /// </summary>
    public CompositorVisualElement GetWindow(VisualElementRetention retention, CompositorWindowInfo window)
    {
        return CompositorVisualElement.GetOrCreate(retention, this, window);
    }

    /// <inheritdoc />
    public void Dispose() => Accessibility.Dispose();
    private VisualElementQueryResult? Acquire(
        VisualElementRetention retention,
        VisualElementLocator locator,
        VisualElementResolution resolution,
        VisualElementQueryRequest request,
        int excludedProcessId)
    {
        var desktop = ReadDesktop();
        var point = locator.Kind is VisualElementLocatorKind.Point ? locator.Point : ReadPointer();
        if (resolution == VisualElementResolution.Screen
            || locator.Kind == VisualElementLocatorKind.Default && resolution == VisualElementResolution.Direct)
        {
            var monitor = desktop.Outputs.FirstOrDefault(item => item.Bounds.Contains(point)) ?? desktop.Outputs.FirstOrDefault();
            return monitor is null ? null : CompositorVisualElement.GetOrCreate(retention, this, monitor).Query(request);
        }

        var window = default(CompositorWindowInfo);
        if (locator.Kind == VisualElementLocatorKind.NativeWindow)
        {
            window = desktop.Windows.FirstOrDefault(item => item.Handle == locator.NativeWindowHandle && item.ProcessId != excludedProcessId);
        }
        else if (locator.Kind is VisualElementLocatorKind.Focused or VisualElementLocatorKind.Default)
        {
            var active = Compositor.GetActiveWindowAsync().GetAwaiter().GetResult();
            window = desktop.Windows.FirstOrDefault(item => item.Id == active?.Id && item.ProcessId != excludedProcessId);
        }
        else
        {
            window = desktop.WindowAt(point, excludedProcessId);
        }

        if (window is null)
        {
            return null;
        }

        if (resolution == VisualElementResolution.TopLevel)
        {
            return GetWindow(retention, window).Query(request);
        }

        var root = default(nint);
        try
        {
            root = Accessibility.Call(() => FindAccessibleWindow(window));
        }
        catch (NotSupportedException)
        {
            return GetWindow(retention, window).Query(request);
        }

        if (root == 0)
        {
            return GetWindow(retention, window).Query(request);
        }

        try
        {
            return QueryAccessible(retention, window, root, locator, point, request);
        }
        catch (VisualElementProviderException exception) when (exception.Kind == VisualElementQueryFailureKind.ElementUnavailable)
        {
            // Enabling a lazy bridge can replace its initial window proxy. The
            // previous Call has returned, so the native dispatcher can drain its
            // events before one cache refresh/reacquisition. Never retry indefinitely.
            root = Accessibility.Call(() => FindAccessibleWindow(window, shouldRefresh: true));
            return root == 0 ? GetWindow(retention, window).Query(request) : QueryAccessible(retention, window, root, locator, point, request);
        }
    }

    /// <summary>
    /// Composes output captures in logical desktop coordinates.
    /// </summary>
    public Task<IVisualElementCapture> CaptureAsync(PixelRect bounds, CancellationToken cancellationToken)
    {
        return LinuxScreenCapture.CaptureAsync(Compositor, ScreenCapturer, bounds, cancellationToken);
    }

    private PixelPoint ReadPointer()
    {
        return Compositor.GetGlobalCursorPositionAsync().GetAwaiter().GetResult();
    }
}
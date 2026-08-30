using System.Collections;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Automation;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.Automation;

internal sealed class TopLevelWindowEnumerator : IVisualElementEnumerator
{
    public VisualElementQueryResult Current
    {
        get
        {
            ThrowIfUnavailable();
            return _current ?? throw new InvalidOperationException("The Enumerator has no current item.");
        }
    }

    object IEnumerator.Current => Current;

    public int Count
    {
        get
        {
            ThrowIfUnavailable();
            return -1;
        }
    }

    public int Index { get; private set; } = -1;

    private readonly WindowsVisualElementBackend _backend;
    private readonly WindowsDisplayTopology _topology;
    private readonly HMONITOR _monitorHandle;
    private readonly GET_WINDOW_CMD _direction;
    private readonly VisualElementQueryRequest _queryRequest;
    private readonly VisualElementRetention _retention;
    private HWND _lastWindow;
    private VisualElementQueryResult? _lookahead;
    private VisualElementQueryResult? _current;
    private bool _shouldStartAtTop;
    private bool _isLookaheadResolved;
    private bool _isCompleted;
    private bool _isDisposed;

    internal TopLevelWindowEnumerator(
        VisualContext context,
        WindowsVisualElementBackend backend,
        WindowsDisplayTopology topology,
        HMONITOR monitorHandle,
        HWND initialWindow,
        bool shouldStartAtTop,
        GET_WINDOW_CMD direction,
        VisualElementQueryRequest queryRequest)
    {
        _backend = backend;
        _topology = topology;
        _monitorHandle = monitorHandle;
        _lastWindow = initialWindow;
        _shouldStartAtTop = shouldStartAtTop;
        _direction = direction;
        _queryRequest = queryRequest;
        _retention = context.CreateRetention();
    }

    public bool HasMore
    {
        get
        {
            ThrowIfUnavailable();
            EnsureLookahead();
            return _lookahead is not null;
        }
    }

    public bool MoveNext()
    {
        ThrowIfUnavailable();
        EnsureLookahead();
        if (_lookahead is not { } next)
        {
            _current = null;
            Index = -1;
            return false;
        }

        _lookahead = null;
        _isLookaheadResolved = false;
        _current = next;
        _lastWindow = (HWND)((UIAutomationVisualElement)next.Element).NativeWindowHandle;
        _shouldStartAtTop = false;
        Index++;
        return true;
    }

    public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lookahead = null;
        _current = null;
        _retention.Dispose();
    }

    private void EnsureLookahead()
    {
        if (_isLookaheadResolved || _isCompleted)
        {
            return;
        }

        _lookahead = QueryTopLevelWindow();
        _isLookaheadResolved = true;
        _isCompleted = _lookahead is null;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (WindowsDisplayTopology.Current.Generation != _topology.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Enumerator was created.");
        }
    }

    private VisualElementQueryResult? QueryTopLevelWindow()
    {
        var windowHandle = FindNextTopLevelWindow();
        if (windowHandle == HWND.Null)
        {
            return null;
        }

        try
        {
            using var cacheRequest = _backend.Automation.CreateElementCacheRequest(_queryRequest.RequestedFields);
            using var cachedElement = _backend.Automation.ElementFromHandleBuildCache(windowHandle, cacheRequest);
            if (!cachedElement.HasValue)
            {
                throw new InvalidOperationException("UI Automation did not return an element for the top-level window.");
            }

            var element = _backend.GetOrCreateUIAutomationElement(_retention, in cachedElement);
            return cachedElement.CreateQueryResult(element, _queryRequest);
        }
        catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
        {
            throw WindowsUIAutomationFailure.CreateException(exception);
        }
    }

    private HWND FindNextTopLevelWindow()
    {
        var candidate = _shouldStartAtTop ? PInvoke.GetTopWindow(HWND.Null) : PInvoke.GetWindow(_lastWindow, _direction);
        while (candidate != HWND.Null)
        {
            if (_topology.FindTopLevelWindowDisplay(candidate)?.MonitorHandle == _monitorHandle)
            {
                return candidate;
            }

            candidate = PInvoke.GetWindow(candidate, _direction);
        }

        return HWND.Null;
    }
}
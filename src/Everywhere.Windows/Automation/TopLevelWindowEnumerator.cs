using System.Collections;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Automation;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.Automation;

internal sealed class TopLevelWindowEnumerator : IVisualElementCursor
{
    public VisualElementQueryResult Current
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            return _current ?? throw new InvalidOperationException("The Enumerator has no current item.");
        }
    }

    object IEnumerator.Current => Current;

    public int Count => -1;

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
            throw new VisualElementProviderException(
                VisualElementQueryFailureKind.ElementUnavailable,
                "The display topology changed after this Enumerator was created.");
        }
    }

    private VisualElementQueryResult? QueryTopLevelWindow()
    {
        while (true)
        {
            var windowHandle = FindNextTopLevelWindow();
            if (windowHandle == HWND.Null) return null;
            _lastWindow = windowHandle;
            _shouldStartAtTop = false;

            try
            {
                using var cacheRequest = _backend.Automation.CreateElementCacheRequest(_queryRequest.RequestedFields);
                using var cachedElement = _backend.Automation.ElementFromHandleBuildCache(windowHandle, cacheRequest);
                if (!cachedElement.HasValue) continue;

                var element = _backend.GetOrCreateUIAutomationElement(_retention, in cachedElement);
                return cachedElement.CreateQueryResult(element, _queryRequest);
            }
            catch (Exception exception) when (WindowsUIAutomationFailure.IsElementUnavailable(exception))
            {
                // The Win32 cursor has already advanced, so this vanished window can be skipped safely.
            }
            catch (Exception exception) when (WindowsUIAutomationFailure.IsProviderException(exception))
            {
                throw WindowsUIAutomationFailure.CreateException(exception);
            }
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
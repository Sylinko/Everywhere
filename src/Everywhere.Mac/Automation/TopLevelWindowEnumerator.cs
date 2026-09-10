using System.Collections;
using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Enumerates Context-owned AXWindow elements in one operation-local Quartz Z-order observation.
/// </summary>
public sealed class TopLevelWindowEnumerator : IVisualElementEnumerator
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

    private readonly MacVisualElementBackend _backend;
    private readonly CGDisplayTopology _topology;
    private readonly CGWindowZOrder _windowZOrder;
    private readonly AXWindowResolver _windowResolver;
    private readonly uint _displayId;
    private readonly int _direction;
    private readonly VisualElementQueryRequest _queryRequest;
    private readonly VisualElementRetention _retention;
    private VisualElementQueryResult? _lookahead;
    private VisualElementQueryResult? _current;
    private int _nextWindowIndex;
    private bool _isLookaheadResolved;
    private bool _isCompleted;
    private bool _isDisposed;

    private TopLevelWindowEnumerator(
        VisualContext context,
        MacVisualElementBackend backend,
        CGDisplayTopology topology,
        CGWindowZOrder windowZOrder,
        uint displayId,
        int nextWindowIndex,
        int direction,
        VisualElementQueryRequest queryRequest)
    {
        _backend = backend;
        _topology = topology;
        _windowZOrder = windowZOrder;
        _windowResolver = new AXWindowResolver(windowZOrder.Windows);
        _displayId = displayId;
        _nextWindowIndex = nextWindowIndex;
        _direction = direction;
        _queryRequest = queryRequest;
        _retention = context.CreateRetention();
    }

    public static IVisualElementEnumerator CreateChildren(
        VisualContext context,
        MacVisualElementBackend backend,
        CGDisplayTopology topology,
        uint displayId,
        VisualElementQueryRequest queryRequest)
    {
        var windowZOrder = CGWindowZOrder.Capture(topology);
        return new TopLevelWindowEnumerator(context, backend, topology, windowZOrder, displayId, 0, 1, queryRequest);
    }

    public static IVisualElementEnumerator CreateSiblings(
        VisualContext context,
        MacVisualElementBackend backend,
        CGDisplayTopology topology,
        uint originWindowId,
        VisualElementRelation relation,
        VisualElementQueryRequest queryRequest)
    {
        var windowZOrder = CGWindowZOrder.Capture(topology);
        var originIndex = -1;
        CGWindowZOrder.Entry? origin = null;
        for (var index = 0; index < windowZOrder.Windows.Count; index++)
        {
            if (windowZOrder.Windows[index].WindowId != originWindowId)
            {
                continue;
            }

            originIndex = index;
            origin = windowZOrder.Windows[index];
            break;
        }

        if (origin is null)
        {
            return EmptyVisualElementEnumerator.Shared;
        }

        var direction = relation switch
        {
            VisualElementRelation.PreviousSibling => -1,
            VisualElementRelation.NextSibling => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };
        return new TopLevelWindowEnumerator(
            context,
            backend,
            topology,
            windowZOrder,
            origin.Display.DisplayId,
            originIndex + direction,
            direction,
            queryRequest);
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
        _windowResolver.Dispose();
        _retention.Dispose();
    }

    private void EnsureLookahead()
    {
        if (_isLookaheadResolved || _isCompleted)
        {
            return;
        }

        try
        {
            _lookahead = QueryNextWindow();
            _isLookaheadResolved = true;
            _isCompleted = _lookahead is null;
        }
        catch (AXException exception)
        {
            throw exception.CreateException();
        }
    }

    private VisualElementQueryResult? QueryNextWindow()
    {
        while (_nextWindowIndex >= 0 && _nextWindowIndex < _windowZOrder.Windows.Count)
        {
            var window = _windowZOrder.Windows[_nextWindowIndex];
            _nextWindowIndex += _direction;
            if (window.Display.DisplayId != _displayId)
            {
                continue;
            }

            using var nativeWindow = _windowResolver.Resolve(window);
            if (nativeWindow is null)
            {
                continue;
            }

            return _backend.GetOrCreateAXElement(_retention, nativeWindow).Query(_queryRequest);
        }

        return null;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (CGDisplayTopology.Current.Generation != _topology.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Enumerator was created.");
        }
    }
}
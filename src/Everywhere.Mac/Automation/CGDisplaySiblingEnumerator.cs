using System.Collections;
using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Enumerates sibling screens from one immutable display-topology generation.
/// </summary>
public sealed class CGDisplaySiblingEnumerator(
    VisualContext context,
    MacVisualElementBackend backend,
    CGDisplayTopology topology,
    int nextDisplayIndex,
    int direction,
    VisualElementQueryRequest queryRequest
) : IVisualElementCursor
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

    public int Count { get; } = direction < 0 ? Math.Max(0, nextDisplayIndex + 1) : Math.Max(0, topology.Displays.Count - nextDisplayIndex);

    public int Index { get; private set; } = -1;

    private readonly VisualElementRetention _retention = context.CreateRetention();
    private VisualElementQueryResult? _current;
    private int _nextDisplayIndex = nextDisplayIndex;
    private bool _isDisposed;

    public bool MoveNext()
    {
        ThrowIfUnavailable();
        if (_nextDisplayIndex < 0 || _nextDisplayIndex >= topology.Displays.Count)
        {
            _current = null;
            Index = -1;
            return false;
        }

        var element = backend.GetOrCreateScreenElement(_retention, topology, topology.Displays[_nextDisplayIndex]);
        _current = element.Query(queryRequest);
        _nextDisplayIndex += direction;
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
        _current = null;
        _retention.Dispose();
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (CGDisplayTopology.Current.Generation != topology.Generation)
        {
            throw new VisualElementProviderException(
                VisualElementQueryFailureKind.ElementUnavailable,
                "The display topology changed after this Enumerator was created.");
        }
    }
}
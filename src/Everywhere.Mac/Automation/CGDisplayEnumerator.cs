using System.Collections;
using Everywhere.Automation;
using Everywhere.Mac.Interop;
using CGDisplay = Everywhere.Mac.Interop.CGDisplay;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Resolves the single current Screen parent of an on-screen AXWindow.
/// </summary>
public sealed class CGDisplayEnumerator(
    VisualContext context,
    MacVisualElementBackend backend,
    CGDisplayTopology topology,
    uint windowId,
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

    public int Count => _display is null ? 0 : 1;

    public int Index { get; private set; } = -1;

    private readonly CGDisplay? _display = CGWindowZOrder.Capture(topology).Find(windowId)?.Display;
    private readonly VisualElementRetention _retention = context.CreateRetention();
    private VisualElementQueryResult? _current;
    private bool _isCompleted;
    private bool _isDisposed;

    public bool MoveNext()
    {
        ThrowIfUnavailable();
        if (_isCompleted || _display is null)
        {
            _current = null;
            Index = -1;
            return false;
        }

        _isCompleted = true;
        _current = backend.GetOrCreateScreenElement(_retention, topology, _display).Query(queryRequest);
        Index = 0;
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
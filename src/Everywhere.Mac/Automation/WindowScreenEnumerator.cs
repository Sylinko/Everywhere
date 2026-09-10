using System.Collections;
using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Resolves the single current Screen parent of an on-screen AXWindow.
/// </summary>
public sealed class WindowScreenEnumerator(
    VisualContext context,
    MacVisualElementBackend backend,
    CGDisplayTopology topology,
    uint windowId,
    VisualElementQueryRequest queryRequest
) : IVisualElementEnumerator
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
            return _display is null ? 0 : 1;
        }
    }

    public int Index { get; private set; } = -1;

    private readonly MacDisplay? _display = CGWindowZOrder.Capture(topology).Find(windowId)?.Display;
    private readonly VisualElementRetention _retention = context.CreateRetention();
    private VisualElementQueryResult? _current;
    private bool _isCompleted;
    private bool _isDisposed;

    public bool HasMore
    {
        get
        {
            ThrowIfUnavailable();
            return !_isCompleted && _display is not null;
        }
    }

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
            throw new InvalidOperationException("The display topology changed after this Enumerator was created.");
        }
    }
}
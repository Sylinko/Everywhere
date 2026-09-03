using System.Collections;
using System.Diagnostics.CodeAnalysis;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Input;
using Everywhere.Automation;
using Everywhere.Windows.Interop;
using AutomationLocaleKey = Everywhere.Automation.I18N.LocaleKey;

namespace Everywhere.Windows.Automation;

/// <summary>
/// Represents one Context-owned Windows display backed by an <see cref="HMONITOR" /> identity.
/// </summary>
public sealed class ScreenVisualElement : VisualElement
{
    /// <summary>
    /// Gets the topology generation number that was current when this Screen element was observed.
    /// </summary>
    public long TopologyGeneration { get; }

    /// <summary>
    /// Gets the <see cref="HMONITOR" /> handle that identifies this Screen element.
    /// </summary>
    public nint MonitorHandle => Display.MonitorHandle;

    /// <summary>
    /// Gets the bounding rectangle of this Screen element.
    /// </summary>
    public PixelRect Bounds => Display.Bounds;

    private WindowsVisualElementBackend Backend { get; }

    private WindowsDisplay Display { get; }

    internal ScreenVisualElement(
        VisualContext context,
        WindowsVisualElementBackend backend,
        long topologyGeneration,
        WindowsDisplay display)
        : base(context, $"screen:{topologyGeneration}:{display.MonitorHandle}")
    {
        Backend = backend;
        TopologyGeneration = topologyGeneration;
        Display = display;
    }

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => QueryCurrent(request);

    private VisualElementQueryResult QueryCurrent(VisualElementQueryRequest request)
    {
        var topology = WindowsDisplayTopology.Current;
        if (TopologyGeneration != topology.Generation)
        {
            return new VisualElementQueryResult(
                this,
                default,
                VisualElementFields.None,
                request.RequestedFields,
                new VisualElementQueryFailure(
                    VisualElementQueryFailureKind.ElementUnavailable,
                    new DynamicLocaleKey(AutomationLocaleKey.VisualContext_QueryFailure_ElementUnavailable),
                    new InvalidOperationException("The display topology changed after this Screen element was observed.")));
        }

        return CreateQueryResult(request);
    }

    private VisualElementQueryResult CreateQueryResult(VisualElementQueryRequest request)
    {
        var requestedFields = request.RequestedFields;
        var availableFields = VisualElementFields.None;
        var id = default(string);
        var type = default(VisualElementType?);
        var states = default(VisualElementStates?);
        var bounds = default(PixelRect?);

        if (requestedFields.HasFlag(VisualElementFields.Id))
        {
            id = Id;
            availableFields |= VisualElementFields.Id;
        }

        if (requestedFields.HasFlag(VisualElementFields.Type))
        {
            type = VisualElementType.Screen;
            availableFields |= VisualElementFields.Type;
        }

        if (requestedFields.HasFlag(VisualElementFields.States))
        {
            states = VisualElementStates.None;
            availableFields |= VisualElementFields.States;
        }

        if (requestedFields.HasFlag(VisualElementFields.Bounds))
        {
            bounds = Bounds;
            availableFields |= VisualElementFields.Bounds;
        }

        return new VisualElementQueryResult(
            this,
            new VisualElementSnapshot(id, type, states, null, null, false, bounds, null, null),
            availableFields,
            requestedFields & ~availableFields,
            null);
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementEnumerationOptions options)
    {
        var topology = WindowsDisplayTopology.Current;
        if (TopologyGeneration != topology.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Screen element was observed.");
        }

        return relation switch
        {
            VisualElementRelation.Parent => new EmptyVisualElementEnumerator(),
            VisualElementRelation.Child => new TopLevelWindowEnumerator(
                Context,
                Backend,
                topology,
                (HMONITOR)MonitorHandle,
                HWND.Null,
                true,
                GET_WINDOW_CMD.GW_HWNDNEXT,
                options.QueryRequest),
            VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling => CreateSiblingEnumerator(
                topology,
                relation,
                options.QueryRequest),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };
    }

    /// <inheritdoc />
    public override void Invoke() => ThrowActionNotSupported(nameof(Invoke));

    /// <inheritdoc />
    public override void SetText(string text) => ThrowActionNotSupported(nameof(SetText));

    /// <inheritdoc />
    public override void Focus() => ThrowActionNotSupported(nameof(Focus));

    /// <inheritdoc />
    public override void SendKeyGesture(KeyGesture keyGesture) => ThrowActionNotSupported(nameof(SendKeyGesture));

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var topology = WindowsDisplayTopology.Current;
        if (TopologyGeneration != topology.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Screen element was observed.");
        }

        return Task.FromResult<IVisualElementCapture>(
            GDIScreenCapture.Capture(Bounds) ?? throw new InvalidOperationException("The display does not intersect the Windows virtual screen."));
    }

    /// <inheritdoc />
    protected override void ReleaseCore()
    {
        // HMONITOR is a borrowed pseudo-handle and has no corresponding release operation.
    }

    private IVisualElementEnumerator CreateSiblingEnumerator(
        WindowsDisplayTopology topology,
        VisualElementRelation relation,
        VisualElementQueryRequest request)
    {
        var originIndex = -1;
        for (var index = 0; index < topology.Displays.Count; index++)
        {
            if (topology.Displays[index].MonitorHandle != MonitorHandle) continue;
            originIndex = index;
            break;
        }
        var direction = relation == VisualElementRelation.PreviousSibling ? -1 : 1;
        return originIndex < 0 ?
            new EmptyVisualElementEnumerator() :
            new ScreenSiblingEnumerator(Context, Backend, topology, originIndex + direction, direction, request);
    }

    [DoesNotReturn]
    private static void ThrowActionNotSupported(string action) =>
        throw new NotSupportedException($"A Screen visual element does not support the '{action}' action.");

    private sealed class ScreenSiblingEnumerator(
        VisualContext context,
        WindowsVisualElementBackend backend,
        WindowsDisplayTopology topology,
        int nextMonitorIndex,
        int direction,
        VisualElementQueryRequest queryRequest
    ) : IVisualElementEnumerator
    {
        public VisualElementQueryResult Current => _current ?? throw new InvalidOperationException("The Enumerator has no current item.");

        object IEnumerator.Current => Current;

        public int Count { get; } = direction < 0 ? Math.Max(0, nextMonitorIndex + 1) : Math.Max(0, topology.Displays.Count - nextMonitorIndex);

        public int Index { get; private set; } = -1;

        private VisualElementQueryResult? _current;
        private readonly VisualElementRetention _retention = context.CreateRetention();
        private int _nextMonitorIndex = nextMonitorIndex;
        private bool _isDisposed;

        public bool HasMore
        {
            get
            {
                ThrowIfUnavailable();
                return _nextMonitorIndex >= 0 && _nextMonitorIndex < topology.Displays.Count;
            }
        }

        public bool MoveNext()
        {
            ThrowIfUnavailable();
            if (_nextMonitorIndex < 0 || _nextMonitorIndex >= topology.Displays.Count)
            {
                _current = null;
                Index = -1;
                return false;
            }

            var element = backend.GetOrCreateScreenElement(_retention, topology, topology.Displays[_nextMonitorIndex]);
            _current = element.Query(queryRequest);
            _nextMonitorIndex += direction;
            Index++;
            return true;
        }

        public void Reset() => throw new NotSupportedException("Visual relation enumerators cannot be reset.");

        public void Dispose()
        {
            _isDisposed = true;
            _current = null;
            _retention.Dispose();
        }

        private void ThrowIfUnavailable()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
        }
    }
}
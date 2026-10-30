using Avalonia;
using Everywhere.Automation;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Automation;

/// <summary>
/// A Context-owned output or window, including applications without accessibility support.
/// </summary>
public sealed class CompositorVisualElement : VisualElement
{
    private readonly LinuxVisualElementBackend _backend;
    private readonly CompositorWindowInfo? _window;
    private readonly OutputInfo? _output;

    private CompositorVisualElement(
        VisualElementIdentity identity,
        LinuxVisualElementBackend backend,
        CompositorWindowInfo? window,
        OutputInfo? output) : base(identity, backend.AllocateId())
    {
        _backend = backend;
        _window = window;
        _output = output;
    }

    /// <summary>
    /// Retains a compositor window in the context identity map.
    /// </summary>
    public static CompositorVisualElement GetOrCreate(
        VisualElementRetention retention,
        LinuxVisualElementBackend backend,
        CompositorWindowInfo window)
    {
        return retention.Context.GetIdentityMap<WindowIdentity>()
            .GetOrAdd(
                retention,
                new WindowIdentity(backend.Compositor.InstanceId, window.Id, window.ProcessId),
                (Backend: backend, Window: window),
                static (identity, state) => new CompositorVisualElement(identity, state.Backend, state.Window, null));
    }

    /// <summary>
    /// Retains an output with its observed geometry in the context identity map.
    /// </summary>
    public static CompositorVisualElement GetOrCreate(VisualElementRetention retention, LinuxVisualElementBackend backend, OutputInfo output)
    {
        return retention.Context.GetIdentityMap<OutputIdentity>()
            .GetOrAdd(
                retention,
                new OutputIdentity(backend.Compositor.InstanceId, output.Name, output.Bounds),
                (Backend: backend, Output: output),
                static (identity, state) => new CompositorVisualElement(identity, state.Backend, null, state.Output));
    }

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
    {
        var desktop = _backend.ReadDesktop();
        var window = FindWindow(desktop);
        var output = _output is null ? null : desktop.Outputs.FirstOrDefault(item => item.Name == _output.Name && item.Bounds == _output.Bounds);
        if (window is null && output is null)
        {
            throw new VisualElementProviderException(
                VisualElementQueryFailureKind.ElementUnavailable,
                "The compositor target disappeared or its output geometry changed.");
        }

        var available = request.RequestedFields & ~VisualElementFields.Text;
        var snapshot = new VisualElementSnapshot(
            Id,
            window is null ? VisualElementType.Screen : VisualElementType.TopLevel,
            VisualElementStates.None,
            window?.Title ?? output?.Name,
            null,
            false,
            window?.SelectionBounds ?? output?.Bounds,
            window?.ProcessId ?? 0,
            window?.Handle ?? 0);
        return new(this, snapshot, available, request.RequestedFields & ~available, null);
    }

    /// <inheritdoc />
    protected override IVisualElementCursor CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementQueryRequest request,
        int offset,
        CancellationToken cancellationToken)
    {
        return new LinuxVisualCursor(Context, retention => Enumerate(retention, relation), request, offset, cancellationToken);
    }

    private IEnumerable<VisualElement> Enumerate(VisualElementRetention retention, VisualElementRelation relation)
    {
        var desktop = _backend.ReadDesktop();
        var window = FindWindow(desktop);
        if (_window is not null && window is null)
        {
            throw new VisualElementProviderException(VisualElementQueryFailureKind.ElementUnavailable, "The source window disappeared.");
        }

        if (relation is VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling)
        {
            var step = relation == VisualElementRelation.PreviousSibling ? -1 : 1;
            if (_output is not null)
            {
                var items = desktop.Outputs.ToArray();
                var position = Array.FindIndex(items, item => item.Name == _output.Name);
                for (var index = position + step; position >= 0 && index >= 0 && index < items.Length; index += step)
                {
                    yield return GetOrCreate(retention, _backend, items[index]);
                }
            }
            else if (window is not null)
            {
                var items = desktop.Windows.Where(item => item.OutputName == window.OutputName).ToArray();
                var position = Array.FindIndex(items, item => item.Id == window.Id);
                for (var index = position + step; position >= 0 && index >= 0 && index < items.Length; index += step)
                {
                    yield return GetOrCreate(retention, _backend, items[index]);
                }
            }

            yield break;
        }

        if (relation == VisualElementRelation.Parent && window is not null)
        {
            var output = desktop.Outputs.FirstOrDefault(item => item.Name == window.OutputName);
            if (output is not null)
            {
                yield return GetOrCreate(retention, _backend, output);
            }
        }

        if (relation != VisualElementRelation.Child)
        {
            yield break;
        }

        if (_output is not null)
        {
            foreach (var child in desktop.Windows.Where(item => item.OutputName == _output.Name))
            {
                yield return GetOrCreate(retention, _backend, child);
            }
        }
        else if (window is not null)
        {
            var pointer = default(nint);
            try
            {
                pointer = _backend.Accessibility.Call(() => _backend.FindAccessibleWindow(window));
            }
            catch (NotSupportedException)
            {
                yield break;
            }

            if (pointer == 0)
            {
                yield break;
            }

            try
            {
                yield return AtspiVisualElement.GetOrCreate(retention, _backend, window, pointer);
            }
            finally
            {
                _backend.Accessibility.Release(pointer);
            }
        }
    }

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        return _backend.CaptureAsync(QueryCore(new(VisualElementFields.Bounds, 0)).Snapshot.Bounds ?? default, cancellationToken);
    }

    /// <inheritdoc />
    protected override VisualElement AdoptCore(VisualElementRetention destinationRetention)
    {
        return _window is not null
            ? GetOrCreate(destinationRetention, _backend, _window)
            : GetOrCreate(destinationRetention, _backend, _output ?? throw new InvalidOperationException());
    }

    /// <inheritdoc />
    protected override void ReleaseCore()
    {
    }

    private CompositorWindowInfo? FindWindow(DesktopSnapshot desktop)
    {
        return _window is null ? null : desktop.Windows.FirstOrDefault(item => item.Id == _window.Id && item.ProcessId == _window.ProcessId);
    }

    private sealed record WindowIdentity(string Instance, string Id, int ProcessId);

    private sealed record OutputIdentity(string Instance, string Name, PixelRect Bounds);
}
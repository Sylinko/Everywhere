using System.Diagnostics.CodeAnalysis;
using Avalonia;
using Everywhere.Automation;
using Everywhere.Mac.Interop;

namespace Everywhere.Mac.Automation;

/// <summary>
/// Represents one Context-owned macOS display without pretending that NSScreen is an AX provider.
/// </summary>
public sealed class ScreenVisualElement : VisualElement
{
    /// <summary>
    /// Gets the topology generation that was current when this Screen element was observed.
    /// </summary>
    public long TopologyGeneration { get; }

    /// <summary>
    /// Gets the Core Graphics display identifier of this Screen element.
    /// </summary>
    public uint DisplayId => Display.DisplayId;

    /// <summary>
    /// Gets the bounding rectangle captured for this Screen element.
    /// </summary>
    public PixelRect Bounds => Display.Bounds;

    private MacVisualElementBackend Backend { get; }

    private MacDisplay Display { get; }

    private ScreenVisualElement(
        VisualElementIdentity identity,
        MacVisualElementBackend backend,
        long topologyGeneration,
        MacDisplay display,
        string id
    ) : base(identity, id)
    {
        Backend = backend;
        TopologyGeneration = topologyGeneration;
        Display = display;
    }

    public static ScreenVisualElement GetOrCreate(
        VisualElementRetention retention,
        MacVisualElementBackend backend,
        CGDisplayTopology topology,
        MacDisplay display)
    {
        var context = retention.Context;
        return context.GetIdentityMap<ScreenIdentity>().GetOrAdd(
            retention,
            new ScreenIdentity(topology.Generation, display.DisplayId),
            (Backend: backend, Display: display),
            static (identity, state) => new ScreenVisualElement(
                identity,
                state.Backend,
                identity.Value.TopologyGeneration,
                state.Display,
                state.Backend.AllocateVisualElementId("screen")));
    }

    /// <inheritdoc />
    protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
    {
        if (TopologyGeneration != CGDisplayTopology.Current.Generation)
        {
            return new VisualElementQueryResult(
                this,
                default,
                VisualElementFields.None,
                request.RequestedFields,
                new VisualElementQueryFailure(
                    VisualElementQueryFailureKind.ElementUnavailable,
                    null,
                    new InvalidOperationException("The display topology changed after this Screen element was observed.")));
        }

        var requestedFields = request.RequestedFields;
        var availableFields = VisualElementFields.None;
        var id = default(string);
        var type = default(VisualElementType?);
        var states = default(VisualElementStates?);
        var name = default(string);
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

        if (requestedFields.HasFlag(VisualElementFields.Name))
        {
            name = Display.Name;
            availableFields |= VisualElementFields.Name;
        }

        if (requestedFields.HasFlag(VisualElementFields.Bounds))
        {
            bounds = Bounds;
            availableFields |= VisualElementFields.Bounds;
        }

        return new VisualElementQueryResult(
            this,
            new VisualElementSnapshot(id, type, states, name, null, false, bounds, null, null),
            availableFields,
            requestedFields & ~availableFields,
            null);
    }

    /// <inheritdoc />
    protected override IVisualElementEnumerator CreateEnumeratorCore(
        VisualElementRelation relation,
        VisualElementQueryRequest request)
    {
        var topology = CGDisplayTopology.Current;
        if (TopologyGeneration != topology.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Screen element was observed.");
        }

        return relation switch
        {
            VisualElementRelation.Parent => EmptyVisualElementEnumerator.Shared,
            VisualElementRelation.Child => TopLevelWindowEnumerator.CreateChildren(
                Context,
                Backend,
                topology,
                DisplayId,
                request),
            VisualElementRelation.PreviousSibling or VisualElementRelation.NextSibling => CreateSiblingEnumerator(
                topology,
                relation,
                request),
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, null),
        };
    }

    /// <inheritdoc />
    protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfTopologyChanged();
        var bounds = Bounds;
        var rectangle = new CGRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);

#pragma warning disable CA1422
        using var image = CGImage.ScreenImage(0, rectangle, CGWindowListOption.OnScreenOnly, CGWindowImageOption.Default);
#pragma warning restore CA1422

        return image is null ?
            Task.FromException<IVisualElementCapture>(new InvalidOperationException("Failed to capture the macOS display.")) :
            // Bounds already uses the top-left desktop convention consumed by Avalonia, not backing pixels.
            // The explicit window-list overload is required: the two-parameter overload treats zero as
            // a relative window number and returns null even when screen-capture access is authorized.
            // TODO(macOS): Verify ScreenImage coverage/orientation on rotated and mixed-density displays.
            Task.FromResult<IVisualElementCapture>(new CapturedBitmapData(image, bounds));
    }

    /// <inheritdoc />
    protected override bool TryConvertPlatformException(Exception exception, [NotNullWhen(true)] out Exception? convertedException)
    {
        convertedException = null;
        return false;
    }

    /// <inheritdoc />
    protected override void ReleaseCore()
    {
        // The element owns only immutable scalar topology data and no native display reference.
    }

    private void ThrowIfTopologyChanged()
    {
        if (TopologyGeneration != CGDisplayTopology.Current.Generation)
        {
            throw new InvalidOperationException("The display topology changed after this Screen element was observed.");
        }
    }

    [Serializable]
    private readonly record struct ScreenIdentity(long TopologyGeneration, uint DisplayId);

    private IVisualElementEnumerator CreateSiblingEnumerator(
        CGDisplayTopology topology,
        VisualElementRelation relation,
        VisualElementQueryRequest request)
    {
        var originIndex = -1;
        for (var index = 0; index < topology.Displays.Count; index++)
        {
            if (topology.Displays[index].DisplayId != DisplayId)
            {
                continue;
            }

            originIndex = index;
            break;
        }

        if (originIndex < 0)
        {
            return EmptyVisualElementEnumerator.Shared;
        }

        var direction = relation == VisualElementRelation.PreviousSibling ? -1 : 1;
        return new ScreenSiblingEnumerator(
            Context,
            Backend,
            topology,
            originIndex + direction,
            direction,
            request);
    }
}
using Everywhere.ProcessIsolation;
using Everywhere.ProcessIsolation.Roles;
using MessagePack;
using Serilog;

namespace Everywhere.Automation;

/// <summary>
/// Describes one bounded structural query over a published visual target.
/// </summary>
[MessagePackObject]
public sealed partial record VisualQueryRequest
{
    /// <summary>
    /// Gets the default maximum number of observed nodes returned by one query.
    /// </summary>
    public const int DefaultLimit = 128;

    /// <summary>
    /// Gets the hard maximum node limit accepted from an Agent call.
    /// </summary>
    public const int MaximumLimit = 1024;

    /// <summary>
    /// Gets the relations that may be observed around the query anchors.
    /// </summary>
    [Key(0)]
    public VisualContextTraverseDirections Directions { get; init; }

    /// <summary>
    /// Gets the zero-based offset applied independently to each requested initial relation.
    /// </summary>
    [Key(1)]
    public int Offset { get; init; }

    /// <summary>
    /// Gets the requested maximum observed node count. Values above <see cref="MaximumLimit" /> are clamped.
    /// </summary>
    [Key(2)]
    public int Limit { get; init; }

    /// <summary>
    /// Gets whether every represented element with observed bounds includes a model-facing bounding box.
    /// </summary>
    [Key(3)]
    public bool IncludesBoundingBox { get; init; }

    /// <summary>Creates a structural query with the Agent-facing defaults.</summary>
    public VisualQueryRequest()
    {
        Directions = VisualContextTraverseDirections.All;
        Limit = DefaultLimit;
    }

    public int GetNormalizedLimit()
    {
        ArgumentOutOfRangeException.ThrowIfNegative(Offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Limit);
        return Math.Min(Limit, MaximumLimit);
    }
}

/// <summary>
/// Coordinates bounded structural and text queries within one caller-owned visual Context.
/// </summary>
/// <remarks>
/// The caller owns the Context and active turn. This instance neither disposes them nor advances history. Capture delivery is optional and transfers owned pixel buffers, never native elements.
/// </remarks>
[InHostProcess(ProcessRole.Automation)]
public sealed partial class VisualQuery
{
    private readonly VisualContext _context;
    private readonly Func<IVisualElementCapture, CancellationToken, ValueTask>? _captureReceiver;

    /// <summary>Binds queries to a conversation's identity domain and optionally delivers scan images.</summary>
    /// <param name="context">The caller-owned Context; calls remain serialized by the caller.</param>
    /// <param name="captureReceiver">A single receiver that takes ownership on normal return, including rejected images. On exception, ownership remains with the query. RPC adapters can use the same image-delivery boundary.</param>
    public VisualQuery(VisualContext context, Func<IVisualElementCapture, CancellationToken, ValueTask>? captureReceiver = null)
    {
        _context = context;
        _captureReceiver = captureReceiver;
    }

    /// <summary>Resolves a retained Agent ID and queries its current structure without reconstructing expired targets.</summary>
    public Task<VisualQueryResult> ExecuteAsync(
        int targetId,
        VisualQueryRequest request,
        VisualContextPromptOptions promptOptions,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(ResolveTarget(targetId), request, promptOptions, cancellationToken);

    /// <summary>Queries an already acquired target, including unpublished roots used by host code and probes.</summary>
    public async Task<VisualQueryResult> ExecuteAsync(
        VisualTarget target,
        VisualQueryRequest request,
        VisualContextPromptOptions promptOptions,
        CancellationToken cancellationToken = default)
    {
        var coreElement = target is ElementTarget elementTarget ?
            elementTarget.Element :
            throw new NotSupportedException("This visual element does not expose structural relations.");
        return await BuildAsync([coreElement], request, promptOptions, cancellationToken);
    }

    /// <summary>Observes host-owned attachment/debugger anchors, optionally captures observed TopLevels, and publishes final text.</summary>
    public async Task<VisualQueryResult> BuildAsync(
        IReadOnlyList<VisualElement> originElements,
        VisualQueryRequest request,
        VisualContextPromptOptions promptOptions,
        CancellationToken cancellationToken = default)
    {
        var limit = request.GetNormalizedLimit();
        promptOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var defaultLimits = VisualContextSnapshotLimits.Default;
        var limits = defaultLimits with
        {
            MaximumNodes = limit,
            MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, limit),
        };
        var topLevels = _captureReceiver is null ? null : new List<VisualElementQueryResult>();
        Action<VisualElementQueryResult>? onTopLevelObserved = topLevels is null ? null : topLevels.Add;
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            _context,
            originElements,
            limits,
            request.Directions,
            onTopLevelObserved,
            request.Offset,
            cancellationToken);

        // Snapshot retains these elements throughout serial capture. No effect or background worker
        // can query the Context; only independent owned image buffers leave this method.
        if (topLevels is not null && _captureReceiver is not null)
        {
            foreach (var topLevel in topLevels)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (topLevel.Snapshot.States.GetValueOrDefault().HasFlag(VisualElementStates.Offscreen)) continue;
                var capture = default(IVisualElementCapture);
                try
                {
                    capture = await topLevel.Element.CaptureAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await _captureReceiver(capture, cancellationToken).ConfigureAwait(false);
                    capture = null; // Successful delivery transfers ownership, even when the receiver drops it.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Log.Warning(exception, "Failed to deliver scan capture for {ElementId}", topLevel.Element.Id);
                }
                finally
                {
                    capture?.Dispose();
                }
            }
        }

        var result = VisualContextPromptBuilder.BuildWithResult(_context, snapshot, request, promptOptions, cancellationToken);
        return new VisualQueryResult(result.Content, result.RepresentedTargetCount);
    }

    private VisualTarget ResolveTarget(int targetId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
        return _context.TryGetTarget(targetId, out var target) ?
            target :
            throw new InvalidOperationException($"Visual target {targetId} is no longer available.");
    }
}

/// <summary>Contains the final text and operation-local target count of one structural query.</summary>
public sealed record VisualQueryResult(string Content, int RepresentedTargetCount);
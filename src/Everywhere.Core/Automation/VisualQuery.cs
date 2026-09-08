using Everywhere.Prompting.Documents;

namespace Everywhere.Automation;

/// <summary>
/// Describes one bounded structural query over a published visual target.
/// </summary>
public sealed record VisualQueryRequest
{
    /// <summary>
    /// Gets the default maximum number of observed nodes returned by one query.
    /// </summary>
    public const int DefaultLimit = 128;

    /// <summary>
    /// Gets the hard maximum node limit accepted from an Agent call.
    /// </summary>
    public const int MaximumLimit = 256;

    /// <summary>
    /// Gets the relations that may be observed around the query anchors.
    /// </summary>
    public VisualContextTraverseDirections Directions { get; init; } = VisualContextTraverseDirections.All;

    /// <summary>
    /// Gets the 1-based offset into retained members when the target exposes observed members. Other targets support only offset 1.
    /// </summary>
    public int Offset { get; init; } = 1;

    /// <summary>
    /// Gets the requested maximum observed node count. Values above <see cref="MaximumLimit" /> are clamped.
    /// </summary>
    public int Limit { get; init; } = DefaultLimit;

    internal int GetNormalizedLimit()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Offset);
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
public sealed partial class VisualQuery
{
    private readonly VisualContext _context;
    private readonly Action<IVisualElementCapture>? _captureReceiver;

    /// <summary>Binds queries to a conversation's identity domain and optionally delivers scan images.</summary>
    /// <param name="context">The caller-owned Context; calls remain serialized by the caller.</param>
    /// <param name="captureReceiver">A single receiver that takes ownership on normal return, including rejected images. On exception, ownership remains with the query. RPC adapters can use the same image-delivery boundary.</param>
    public VisualQuery(VisualContext context, Action<IVisualElementCapture>? captureReceiver = null)
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
        var limit = request.GetNormalizedLimit();
        var status = new List<string>();
        var coreElements = GetCoreElements(target, request.Offset, limit, status, out var nextOffset);
        promptOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        if (coreElements.Length == 0) return new VisualQueryResult(CreateStatusResult(promptOptions, status), 0);

        var defaultLimits = VisualContextSnapshotLimits.Default;
        var snapshotLimits = defaultLimits with
        {
            MaximumNodes = limit,
            MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, limit),
        };
        return await BuildAsync(coreElements, promptOptions, snapshotLimits, request.Directions, nextOffset, cancellationToken);
    }

    /// <summary>Observes host-owned attachment/debugger anchors, optionally captures observed TopLevels, and publishes final text.</summary>
    public Task<VisualQueryResult> BuildAsync(
        IReadOnlyList<VisualElement> coreElements,
        VisualContextPromptOptions promptOptions,
        VisualContextSnapshotLimits? limits = null,
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        CancellationToken cancellationToken = default) =>
        BuildAsync(coreElements, promptOptions, limits, directions, null, cancellationToken);

    private async Task<VisualQueryResult> BuildAsync(
        IReadOnlyList<VisualElement> coreElements,
        VisualContextPromptOptions promptOptions,
        VisualContextSnapshotLimits? limits,
        VisualContextTraverseDirections directions,
        int? nextOffset,
        CancellationToken cancellationToken)
    {
        promptOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        var topLevels = _captureReceiver is null ? null : new List<VisualElementQueryResult>();
        Action<VisualElementQueryResult>? onTopLevelObserved = topLevels is null ? null : topLevels.Add;
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            _context,
            coreElements,
            limits,
            directions,
            onTopLevelObserved,
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
                    _captureReceiver(capture);
                    capture = null; // Successful delivery transfers ownership, even when the receiver drops it.
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    Serilog.Log.Warning(exception, "Failed to deliver scan capture for {ElementId}", topLevel.Element.Id);
                }
                finally
                {
                    capture?.Dispose();
                }
            }
        }

        var result = VisualContextPromptBuilder.BuildWithOutcome(_context, snapshot, promptOptions, nextOffset, cancellationToken);
        return new VisualQueryResult(result.Content, result.RepresentedTargetCount);
    }

    private VisualTarget ResolveTarget(int targetId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetId);
        return _context.TryGetTarget(targetId, out var target) ?
            target :
            throw new InvalidOperationException($"Visual target {targetId} is no longer available.");
    }

    private static VisualElement[] GetCoreElements(VisualTarget target, int offset, int limit, List<string> status, out int? nextOffset)
    {
        nextOffset = null;
        if (target is ElementTarget elementTarget)
        {
            if (offset == 1) return [elementTarget.Element];
            status.Add("The requested offset is beyond this visual element's single anchor. Query a returned child ID instead.");
            return [];
        }

        if (target is not CompositeTarget composite)
            throw new NotSupportedException($"Visual target type '{target.GetType().Name}' does not support structural querying.");
        var startIndex = offset - 1;
        if (startIndex >= composite.Parts.Count)
        {
            status.Add($"Offset {offset} is beyond this visual element's {composite.Parts.Count} retained observed members.");
            return [];
        }

        var count = Math.Min(limit, composite.Parts.Count - startIndex);
        var result = new VisualElement[count];
        for (var index = 0; index < count; index++) result[index] = composite.Parts[startIndex + index].Element;
        if (count < composite.Parts.Count - startIndex) nextOffset = offset + count;

        return result;
    }

    private static string CreateStatusResult(VisualContextPromptOptions options, IReadOnlyList<string> items)
    {
        var status = string.Join("; ", items);
        if (status.Length > options.MaximumScalarCharacters) status = status[..options.MaximumScalarCharacters];
        return new PromptTokenLimit(options.TargetTokenBudget, new PromptCompactElement("visual-context").AttributeNotNullOrEmpty("status", status))
            .ToString();
    }
}

/// <summary>Contains the final text and operation-local target count of one structural query.</summary>
public sealed record VisualQueryResult(string Content, int RepresentedTargetCount);
using Everywhere.Automation;
using Everywhere.Prompting.Documents;

namespace Everywhere.Chat;

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
    /// Gets the 1-based offset into retained Composite members. Element targets support only offset 1.
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
/// Executes one bounded structural query over either a real Element target or a logical Composite target.
/// </summary>
/// <remarks>
/// The caller owns the active visual target turn. This operation performs no target lookup and therefore cannot silently reconstruct an unavailable ID.
/// </remarks>
public static class VisualQuery
{
    /// <summary>
    /// Observes and projects the requested target through the canonical Snapshot and PromptNode pipeline.
    /// </summary>
    /// <param name="context">The identity, lifetime, and target domain containing the active turn.</param>
    /// <param name="target">The resolved retained target.</param>
    /// <param name="request">The bounded structural request.</param>
    /// <param name="promptOptions">The prompt projection options.</param>
    /// <param name="onNodeObserved">An optional synchronous observer called after Snapshot completion and before its retention is released.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>Structured model-facing content with atomic target publication.</returns>
    public static PromptNode Execute(
        VisualContext context,
        VisualTarget target,
        VisualQueryRequest request,
        VisualContextPromptOptions promptOptions,
        Action<VisualContextSnapshotNode>? onNodeObserved = null,
        CancellationToken cancellationToken = default)
    {
        var limit = request.GetNormalizedLimit();
        var status = new List<string>(promptOptions.AdditionalStatus);
        AppendDistinctStatus(status, target.Status);
        var coreElements = GetCoreElements(target, request.Offset, limit, status);
        var effectivePromptOptions = promptOptions with { AdditionalStatus = status };
        if (coreElements.Length == 0) return CreateStatusResult(effectivePromptOptions);

        var defaultLimits = VisualContextSnapshotLimits.Default;
        var snapshotLimits = defaultLimits with
        {
            MaximumNodes = limit,
            MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, limit),
        };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, coreElements, snapshotLimits, request.Directions, cancellationToken);
        if (onNodeObserved is not null) ObserveNodes(snapshot.Roots, onNodeObserved);
        return VisualContextPromptBuilder.Build(context, snapshot, effectivePromptOptions, cancellationToken);
    }

    private static VisualElement[] GetCoreElements(VisualTarget target, int offset, int limit, List<string> status)
    {
        if (target is ElementTarget elementTarget)
        {
            if (offset == 1) return [elementTarget.Element];
            status.Add("The requested offset is beyond this Element target's single anchor. Query a returned child ID instead.");
            return [];
        }

        if (target is not CompositeTarget composite)
            throw new NotSupportedException($"Visual target type '{target.GetType().Name}' does not support structural querying.");
        var startIndex = offset - 1;
        if (startIndex >= composite.Parts.Count)
        {
            status.Add($"Composite offset {offset} is beyond its {composite.Parts.Count} retained observed members.");
            return [];
        }

        var count = Math.Min(limit, composite.Parts.Count - startIndex);
        var result = new VisualElement[count];
        for (var index = 0; index < count; index++) result[index] = composite.Parts[startIndex + index].Element;
        var nextOffset = offset + count;
        if (nextOffset <= composite.Parts.Count)
        {
            status.Add(
                $"Composite query selected observed members {offset}-{nextOffset - 1} of {composite.Parts.Count}; continue with offset {nextOffset}.");
        }

        return result;
    }

    private static PromptNode CreateStatusResult(VisualContextPromptOptions options)
    {
        var status = string.Join("; ", options.AdditionalStatus);
        if (status.Length > options.MaximumScalarCharacters) status = status[..options.MaximumScalarCharacters];
        return new PromptTokenLimit(options.TargetTokenBudget, new PromptCompactElement("visual-context").AttributeNotNullOrEmpty("status", status));
    }

    private static void ObserveNodes(IReadOnlyList<VisualContextSnapshotNode> roots, Action<VisualContextSnapshotNode> observer)
    {
        var pending = new Stack<VisualContextSnapshotNode>();
        for (var index = roots.Count - 1; index >= 0; index--) pending.Push(roots[index]);
        while (pending.TryPop(out var node))
        {
            observer(node);
            for (var index = node.Children.Count - 1; index >= 0; index--) pending.Push(node.Children[index]);
        }
    }

    private static void AppendDistinctStatus(List<string> destination, IReadOnlyList<string> source)
    {
        foreach (var item in source)
        {
            if (!string.IsNullOrWhiteSpace(item) && !destination.Contains(item, StringComparer.Ordinal)) destination.Add(item);
        }
    }
}
using Everywhere.Automation;
using Everywhere.Prompting.Documents;

namespace Everywhere.Chat;

/// <summary>
/// Normalizes one bounded visual-context Snapshot, projects structural Composites, builds model-facing prompt content, and atomically publishes every represented target.
/// </summary>
/// <remarks>
/// This builder is a pure in-memory boundary. It must use only facts already present in the supplied Snapshot and must never query a live platform element.
/// </remarks>
public static class VisualContextPromptBuilder
{
    /// <summary>
    /// Builds one bounded prompt projection and commits exactly the Element and Composite targets that survive its local prompt budget.
    /// </summary>
    /// <param name="context">The target and lifetime domain that owns the active Agent turn.</param>
    /// <param name="snapshot">The bounded platform observation retained until target publication completes.</param>
    /// <param name="options">The projection options, or <see langword="null" /> to use <see cref="VisualContextPromptOptions.Default" />.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A structured prompt node whose local limit deterministically preserves every committed target skeleton.</returns>
    public static PromptNode Build(
        VisualContext context,
        VisualContextSnapshot snapshot,
        VisualContextPromptOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? VisualContextPromptOptions.Default;
        effectiveOptions.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        var roots = Normalize(snapshot.Roots, effectiveOptions);
        var allNodes = OrderNodesByRootFairness(roots);
        for (var index = 0; index < allNodes.Length; index++) allNodes[index].RelevanceRank = index;

        var selectedNodes = new HashSet<ProjectionNode>(allNodes, ReferenceEqualityComparer.Instance);
        var contentLimitedNodes = new HashSet<ProjectionNode>(ReferenceEqualityComparer.Instance);
        var hasBudgetOmission = false;
        var maximumAttempts = checked(allNodes.Length * 2 + 2);

        for (var attemptIndex = 0; attemptIndex < maximumAttempts; attemptIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = CreateAttempt(context, snapshot, roots, selectedNodes, contentLimitedNodes, hasBudgetOmission, effectiveOptions);
            var document = new PromptDocument { attempt.Content, };
            var rendered = document.Render(int.MaxValue);
            var includedNodes = new HashSet<PromptNode>(rendered.IncludedNodes, ReferenceEqualityComparer.Instance);
            if (!includedNodes.Contains(attempt.ContextElement))
            {
                throw new PromptBudgetExceededException(
                    $"The visual-context skeleton cannot fit within the target budget of {effectiveOptions.TargetTokenBudget} tokens.");
            }

            var survivingNodes = attempt.TargetElements.AsValueEnumerable().Where(pair => includedNodes.Contains(pair.Key))
                .Select(static pair => pair.Value).ToHashSet(ReferenceEqualityComparer.Instance);
            var missingRequiredNode = selectedNodes.AsValueEnumerable().FirstOrDefault(node => node.IsRequired && !survivingNodes.Contains(node));
            if (missingRequiredNode is not null)
            {
                throw new PromptBudgetExceededException(
                    $"The required visual target '{missingRequiredNode.PrimarySource.Element.Id}' cannot fit within the target budget of {effectiveOptions.TargetTokenBudget} tokens.");
            }

            var newlyLimitedNodes = attempt.ContentNodes
                .AsValueEnumerable()
                .Where(pair => survivingNodes.Contains(pair.Value) &&
                    (rendered.OmittedNodes.Contains(pair.Key) || rendered.TruncatedNodes.Contains(pair.Key)))
                .Select(static pair => pair.Value)
                .Where(node => !contentLimitedNodes.Contains(node))
                .ToArray();
            var hasNewBudgetOmission = survivingNodes.Count != selectedNodes.Count;
            if (!hasNewBudgetOmission && newlyLimitedNodes.Length == 0)
            {
                attempt.Publication.Commit();
                return attempt.Content;
            }

            selectedNodes.IntersectWith(survivingNodes);
            contentLimitedNodes.UnionWith(newlyLimitedNodes);
            hasBudgetOmission = true;
        }

        throw new InvalidOperationException(
            "Visual-context prompt projection did not converge after every monotonic target and content state was exhausted.");
    }

    private static ProjectionNode[] OrderNodesByRootFairness(IReadOnlyList<ProjectionNode> roots)
    {
        if (roots.Count <= 1)
        {
            return EnumerateNodes(roots).OrderBy(static node => node.TraversalPriority).ThenBy(static node => node.TraversalOrdinal).ToArray();
        }

        var nodesByRoot = new ProjectionNode[roots.Count][];
        var totalNodeCount = 0;
        for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
        {
            var nodes = EnumerateNodes([roots[rootIndex]]).OrderBy(static node => node.TraversalPriority).ThenBy(static node => node.TraversalOrdinal)
                .ToArray();
            nodesByRoot[rootIndex] = nodes;
            totalNodeCount += nodes.Length;
        }

        var ordered = new ProjectionNode[totalNodeCount];
        var outputIndex = 0;
        for (var localIndex = 0; outputIndex < ordered.Length; localIndex++)
        {
            foreach (var nodes in nodesByRoot)
            {
                if (localIndex < nodes.Length) ordered[outputIndex++] = nodes[localIndex];
            }
        }

        return ordered;
    }

    private static List<ProjectionNode> Normalize(IReadOnlyList<VisualContextSnapshotNode> roots, VisualContextPromptOptions options)
    {
        var normalized = new List<ProjectionNode>();
        foreach (var root in roots.AsValueEnumerable().OrderBy(static node => node.TraversalOrdinal)) NormalizeNode(root, normalized, options);
        MergeCompositeRuns(normalized, options);
        return normalized;
    }

    private static void NormalizeNode(VisualContextSnapshotNode source, List<ProjectionNode> output, VisualContextPromptOptions options)
    {
        var children = new List<ProjectionNode>();
        foreach (var child in source.Children
                     .AsValueEnumerable()
                     .OrderBy(static node => node.HasSiblingIndex ? node.SiblingIndex : int.MaxValue)
                     .ThenBy(static node => node.TraversalOrdinal))
        {
            NormalizeNode(child, children, options);
        }

        MergeCompositeRuns(children, options);
        if (!ShouldProject(source, children.Count, options.DetailLevel))
        {
            output.AddRange(children);
            return;
        }

        output.Add(new ProjectionNode([source], children, false, null, false));
    }

    private static bool ShouldProject(VisualContextSnapshotNode source, int projectedChildCount, VisualContextDetailLevel detailLevel)
    {
        var snapshot = source.Snapshot;
        var type = snapshot.Type ?? VisualElementType.Unknown;
        if (source.IsCore || source.IsInteractive || type is VisualElementType.Screen or VisualElementType.TopLevel ||
            snapshot.States is not null and not VisualElementStates.None || snapshot.HasMoreText || source.Status.Count > 0 ||
            !string.IsNullOrWhiteSpace(snapshot.Name) || !string.IsNullOrWhiteSpace(snapshot.TextPreview))
        {
            return true;
        }

        return detailLevel switch
        {
            VisualContextDetailLevel.Detailed => projectedChildCount > 0,
            VisualContextDetailLevel.Compact when source.Parent is null => projectedChildCount > 0,
            VisualContextDetailLevel.Compact when type == VisualElementType.Document => projectedChildCount > 0,
            VisualContextDetailLevel.Compact when type == VisualElementType.Panel => projectedChildCount > 1,
            VisualContextDetailLevel.Minimal => source.Parent is null && projectedChildCount > 0,
            _ => false,
        };
    }

    private static void MergeCompositeRuns(List<ProjectionNode> nodes, VisualContextPromptOptions options)
    {
        if (nodes.Count < options.MinimumCompositeMemberCount) return;

        var merged = new List<ProjectionNode>(nodes.Count);
        for (var index = 0; index < nodes.Count;)
        {
            if (!IsCompositeCandidate(nodes[index]))
            {
                merged.Add(nodes[index++]);
                continue;
            }

            var end = index + 1;
            while (end < nodes.Count && IsCompositeCandidate(nodes[end])) end++;
            if (end - index < options.MinimumCompositeMemberCount)
            {
                for (; index < end; index++) merged.Add(nodes[index]);
                continue;
            }

            var sources = new List<VisualContextSnapshotNode>(end - index);
            for (var sourceIndex = index; sourceIndex < end; sourceIndex++) sources.AddRange(nodes[sourceIndex].Sources);
            var (preview, isPreviewTruncated) = CreateCompositePreview(sources, options.MaximumCompositePreviewCharacters);
            merged.Add(new ProjectionNode(sources, [], true, preview, isPreviewTruncated));
            index = end;
        }

        nodes.Clear();
        nodes.AddRange(merged);
    }

    private static bool IsCompositeCandidate(ProjectionNode node) =>
        node is { IsComposite: false, Children.Count: 0, IsCore: false, IsInteractive: false, Type: VisualElementType.Label, Sources.Count: 1 } &&
        !string.IsNullOrWhiteSpace(GetPreferredContent(node.PrimarySource.Snapshot));

    private static (string? Preview, bool IsTruncated) CreateCompositePreview(List<VisualContextSnapshotNode> sources, int maximumCharacters)
    {
        if (maximumCharacters == 0) return (null, sources.Count > 0);

        var builder = new System.Text.StringBuilder(Math.Min(maximumCharacters, 256));
        var isTruncated = false;
        foreach (var source in sources)
        {
            var content = GetPreferredContent(source.Snapshot);
            if (string.IsNullOrWhiteSpace(content)) continue;

            var separatorLength = builder.Length == 0 ? 0 : Environment.NewLine.Length;
            var remaining = maximumCharacters - builder.Length - separatorLength;
            if (remaining <= 0)
            {
                isTruncated = true;
                break;
            }

            if (separatorLength > 0) builder.AppendLine();
            if (content.Length <= remaining)
            {
                builder.Append(content);
                continue;
            }

            builder.Append(content.AsSpan(0, remaining));
            isTruncated = true;
            break;
        }

        return (builder.Length == 0 ? null : builder.ToString(), isTruncated);
    }

    private static BuildAttempt CreateAttempt(
        VisualContext context,
        VisualContextSnapshot snapshot,
        IReadOnlyList<ProjectionNode> roots,
        HashSet<ProjectionNode> selectedNodes,
        HashSet<ProjectionNode> contentLimitedNodes,
        bool hasBudgetOmission,
        VisualContextPromptOptions options)
    {
        var publication = context.BeginPublication();
        var targetElements = new Dictionary<PromptCompactElement, ProjectionNode>(ReferenceEqualityComparer.Instance);
        var contentNodes = new Dictionary<PromptTextChunk, ProjectionNode>(ReferenceEqualityComparer.Instance);
        var contextStatus = GetContextStatus(snapshot, options.AdditionalStatus, hasBudgetOmission, options.MaximumScalarCharacters);
        var contextElement = new PromptCompactElement("visual-context").AttributeNotNullOrEmpty("status", contextStatus);
        AppendProjectedChildren(contextElement, roots, selectedNodes, contentLimitedNodes, publication, targetElements, contentNodes, options);
        var content = new PromptTokenLimit(options.TargetTokenBudget, contextElement);
        return new BuildAttempt(content, contextElement, publication, targetElements, contentNodes);
    }

    private static void AppendProjectedChildren(
        PromptCompactElement parent,
        IReadOnlyList<ProjectionNode> nodes,
        HashSet<ProjectionNode> selectedNodes,
        HashSet<ProjectionNode> contentLimitedNodes,
        VisualTargetPublicationBatch publication,
        Dictionary<PromptCompactElement, ProjectionNode> targetElements,
        Dictionary<PromptTextChunk, ProjectionNode> contentNodes,
        VisualContextPromptOptions options)
    {
        foreach (var node in nodes)
        {
            if (!selectedNodes.Contains(node))
            {
                AppendProjectedChildren(
                    parent,
                    node.Children,
                    selectedNodes,
                    contentLimitedNodes,
                    publication,
                    targetElements,
                    contentNodes,
                    options);
                continue;
            }

            var status = GetNodeStatus(node, contentLimitedNodes.Contains(node));
            var target = CreateTarget(node, status);
            var id = publication.Add(target);
            var element = CreatePromptElement(node, id, status, options, contentNodes);
            targetElements.Add(element, node);
            AppendProjectedChildren(element, node.Children, selectedNodes, contentLimitedNodes, publication, targetElements, contentNodes, options);
            parent.Add(element);
        }
    }

    private static VisualTarget CreateTarget(ProjectionNode node, IReadOnlyList<string> status)
    {
        if (!node.IsComposite)
        {
            return new ElementTarget
            {
                Element = node.PrimarySource.Element,
                Status = status,
            };
        }

        return new CompositeTarget
        {
            Parts = node.Sources.AsValueEnumerable().Select(static source => new CompositePart
            {
                Element = source.Element,
                Snapshot = source.Snapshot,
                IsCore = source.IsCore,
                Status = source.Status.ToArray(),
            }).ToArray(),
            Preview = node.Preview,
            Status = status,
        };
    }

    private static PromptCompactElement CreatePromptElement(
        ProjectionNode node,
        int id,
        IReadOnlyList<string> status,
        VisualContextPromptOptions options,
        Dictionary<PromptTextChunk, ProjectionNode> contentNodes)
    {
        var snapshot = node.PrimarySource.Snapshot;
        var element = new PromptCompactElement(node.IsComposite ? "Composite" : node.Type.ToString())
        {
            Priority = node.IsRequired ? int.MaxValue : Math.Max(1, 1_000_000 - node.RelevanceRank),
        };
        element.Attribute("id", id)
            .AttributeNotNullOrEmpty("name", node.IsComposite ? null : Bound(snapshot.Name, options.MaximumScalarCharacters))
            .AttributeNotNullOrEmpty("status", Bound(string.Join("; ", status), options.MaximumScalarCharacters));
        AppendStateFlags(element, snapshot.States);
        if (node.IsComposite) element.Attribute("observedMembers", node.Sources.Count);
        if (node.Sources.AsValueEnumerable().Any(static source => source.Snapshot.HasMoreText)) element.Flag("moreText");
        if (ShouldIncludeBounds(options.DetailLevel, node.Type) && GetBounds(node) is { } bounds)
        {
            element.Attribute("box", $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}");
        }

        var content = node.IsComposite ? node.Preview : GetElementContent(snapshot);
        if (!string.IsNullOrEmpty(content))
        {
            var contentNode = new PromptTextChunk(content)
            {
                Priority = Math.Max(0, 500_000 - node.RelevanceRank),
            };
            if (content.AsSpan().ContainsAny('\r', '\n')) contentNode.BreakOnLines();
            else contentNode.BreakOnWhitespace();
            contentNodes.Add(contentNode, node);
            element.Add(contentNode);
        }

        return element;
    }

    private static void AppendStateFlags(PromptCompactElement element, VisualElementStates? states)
    {
        if (states is not { } value) return;

        element
            .Flag("offscreen", (value & VisualElementStates.Offscreen) != 0)
            .Flag("disabled", (value & VisualElementStates.Disabled) != 0)
            .Flag("focused", (value & VisualElementStates.Focused) != 0)
            .Flag("selected", (value & VisualElementStates.Selected) != 0)
            .Flag("readOnly", (value & VisualElementStates.ReadOnly) != 0)
            .Flag("password", (value & VisualElementStates.Password) != 0);
    }

    private static List<string> GetNodeStatus(ProjectionNode node, bool isContentLimited)
    {
        var status = new List<string>();
        foreach (var source in node.Sources)
        {
            foreach (var item in source.Status)
            {
                if (!status.Contains(item, StringComparer.Ordinal)) status.Add(item);
            }
        }

        if (node.IsPreviewTruncated) status.Add("Composite preview reached its character limit.");
        if (isContentLimited) status.Add("Content was limited by the prompt budget.");
        return status;
    }

    private static string? GetContextStatus(
        VisualContextSnapshot snapshot,
        IReadOnlyList<string> additionalStatus,
        bool hasBudgetOmission,
        int maximumCharacters)
    {
        var status = new List<string>(snapshot.Status);
        foreach (var item in additionalStatus)
        {
            if (!string.IsNullOrWhiteSpace(item) && !status.Contains(item, StringComparer.Ordinal)) status.Add(item);
        }
        if (!snapshot.IsComplete && status.Count == 0) status.Add("Snapshot observation is incomplete.");
        if (hasBudgetOmission) status.Add("Some visual targets were omitted by the prompt budget.");
        return status.Count == 0 ? null : Bound(string.Join("; ", status), maximumCharacters);
    }

    private static PixelRect? GetBounds(ProjectionNode node)
    {
        PixelRect? result = null;
        foreach (var source in node.Sources)
        {
            if (source.Snapshot.Bounds is not { } bounds) continue;
            result = result is { } current ? current.Union(bounds) : bounds;
        }

        return result;
    }

    private static bool ShouldIncludeBounds(VisualContextDetailLevel detailLevel, VisualElementType type) => detailLevel switch
    {
        VisualContextDetailLevel.Detailed => true,
        VisualContextDetailLevel.Compact when type is VisualElementType.TextEdit or VisualElementType.Button or VisualElementType.CheckBox or
            VisualElementType.ListView or VisualElementType.TreeView or VisualElementType.DataGrid or VisualElementType.TabControl or
            VisualElementType.Table or VisualElementType.Document or VisualElementType.TopLevel or VisualElementType.Screen => true,
        VisualContextDetailLevel.Minimal when type is VisualElementType.TopLevel or VisualElementType.Screen => true,
        _ => false,
    };

    private static string? GetElementContent(VisualElementSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.TextPreview)) return null;
        return ApproximatelyEquals(snapshot.Name, snapshot.TextPreview) ? null : snapshot.TextPreview;
    }

    private static string? GetPreferredContent(VisualElementSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.TextPreview) ? snapshot.TextPreview : snapshot.Name;

    private static bool ApproximatelyEquals(string? first, string? second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)) return false;
        var firstIndex = 0;
        var secondIndex = 0;
        while (true)
        {
            while (firstIndex < first.Length && (char.IsWhiteSpace(first[firstIndex]) || char.IsPunctuation(first[firstIndex]))) firstIndex++;
            while (secondIndex < second.Length && (char.IsWhiteSpace(second[secondIndex]) || char.IsPunctuation(second[secondIndex]))) secondIndex++;
            if (firstIndex == first.Length || secondIndex == second.Length) return firstIndex == first.Length && secondIndex == second.Length;
            if (char.ToLowerInvariant(first[firstIndex++]) != char.ToLowerInvariant(second[secondIndex++])) return false;
        }
    }

    private static string? Bound(string? value, int maximumCharacters) =>
        value is null || value.Length <= maximumCharacters ? value : value[..maximumCharacters];

    private static IEnumerable<ProjectionNode> EnumerateNodes(IEnumerable<ProjectionNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in EnumerateNodes(root.Children)) yield return child;
        }
    }

    private sealed class ProjectionNode(
        IReadOnlyList<VisualContextSnapshotNode> sources,
        List<ProjectionNode> children,
        bool isComposite,
        string? preview,
        bool isPreviewTruncated
    )
    {
        public IReadOnlyList<VisualContextSnapshotNode> Sources { get; } = sources;

        public VisualContextSnapshotNode PrimarySource => Sources[0];

        public List<ProjectionNode> Children { get; } = children;

        public VisualElementType Type => IsComposite ? VisualElementType.Unknown : PrimarySource.Snapshot.Type ?? VisualElementType.Unknown;

        public bool IsComposite { get; } = isComposite;

        public bool IsCore => Sources.AsValueEnumerable().Any(static source => source.IsCore);

        public bool IsInteractive => Sources.AsValueEnumerable().Any(static source => source.IsInteractive);

        public bool IsRequired => IsCore || Type is VisualElementType.Screen or VisualElementType.TopLevel;

        public string? Preview { get; } = preview;

        public bool IsPreviewTruncated { get; } = isPreviewTruncated;

        public float TraversalPriority => Sources.AsValueEnumerable().Min(static source => source.TraversalPriority);

        public long TraversalOrdinal => Sources.AsValueEnumerable().Min(static source => source.TraversalOrdinal);

        public int RelevanceRank { get; set; }
    }

    private sealed record BuildAttempt(
        PromptTokenLimit Content,
        PromptCompactElement ContextElement,
        VisualTargetPublicationBatch Publication,
        IReadOnlyDictionary<PromptCompactElement, ProjectionNode> TargetElements,
        IReadOnlyDictionary<PromptTextChunk, ProjectionNode> ContentNodes
    );
}
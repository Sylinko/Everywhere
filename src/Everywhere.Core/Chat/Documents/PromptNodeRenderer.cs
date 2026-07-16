using System.Security;
using System.Text;
using Everywhere.AI;
using ZLinq;

namespace Everywhere.Chat.Documents;

/// <summary>
/// Materializes and renders declarative prompt node trees without modifying their source tree.
/// </summary>
internal static class PromptNodeRenderer
{
    /// <summary>
    /// Renders a document within a document-wide token budget and reports which source nodes survived.
    /// </summary>
    public static PromptRenderResult Render(PromptDocument document, int maxTokenCount)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTokenCount);

        var truncated = new List<PromptTextChunk>();
        var localLimits = new List<MaterializedTokenLimit>();
        var root = (MaterializedContainer)Materialize(
            document,
            null,
            escapeText: false,
            maxTokenCount,
            truncated,
            localLimits);

        // Limits are collected after their descendants, so nested scopes are reduced before their parents.
        foreach (var localLimit in localLimits)
        {
            // A tighter document budget will necessarily constrain this subtree. Applying a looser local
            // limit first could discard content that should instead compete with the subtree's siblings.
            if (localLimit.MaxTokens > maxTokenCount) continue;
            PruneToBudget(localLimit.Container, localLimit.MaxTokens);
        }

        var (content, tokenCount) = PruneToBudget(root, maxTokenCount);
        return BuildResult(document, root, content, tokenCount, truncated);
    }

    /// <summary>
    /// Renders one node without applying a document-wide token budget while still honoring local limits.
    /// </summary>
    public static string Render(PromptNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is PromptText text) return text.Text;
        if (node is PromptTextChunk { MaxTokens: null } textChunk) return textChunk.Text;

        var localLimits = new List<MaterializedTokenLimit>();
        var root = Materialize(node, null, escapeText: false, int.MaxValue, [], localLimits);
        foreach (var localLimit in localLimits)
        {
            PruneToBudget(localLimit.Container, localLimit.MaxTokens);
        }

        return root.Render();
    }

    private static (string Content, int TokenCount) PruneToBudget(MaterializedContainer container, int maxTokenCount)
    {
        var content = container.Render();
        var tokenCount = TokenHelper.EstimateTokenCount(content);

        while (tokenCount > maxTokenCount)
        {
            var estimatedTokens = tokenCount;
            do
            {
                var candidate = FindLowestPriorityNode(container) ??
                    throw new PromptBudgetExceededException(
                        $"The prompt cannot fit within a budget of {maxTokenCount} tokens because no removable node remains.");
                var removedUpperBound = Math.Max(1, candidate.UpperBoundTokenCount);
                candidate.Remove();

                // Component token counts form a useful upper bound but are not exact after BPE boundary merging.
                // Remove in small batches, then re-render and measure the real combined output.
                estimatedTokens -= (int)Math.Ceiling(removedUpperBound * 1.25);
            }
            while (estimatedTokens > maxTokenCount && !container.IsEmpty);

            content = container.Render();
            tokenCount = TokenHelper.EstimateTokenCount(content);
        }

        return (content, tokenCount);
    }

    private static PromptRenderResult BuildResult(
        PromptDocument document,
        MaterializedContainer root,
        string content,
        int tokenCount,
        IReadOnlyList<PromptTextChunk> truncated)
    {
        var includedSet = new HashSet<PromptNode>(ReferenceEqualityComparer.Instance);
        CollectIncluded(root, includedSet);
        var allNodes = EnumerateSourceNodes(document).ToList();
        return new PromptRenderResult
        {
            Content = content,
            TokenCount = tokenCount,
            IncludedNodes = allNodes.AsValueEnumerable().Where(includedSet.Contains).ToList(),
            OmittedNodes = allNodes.AsValueEnumerable().Where(node => !includedSet.Contains(node)).ToList(),
            TruncatedNodes = truncated.AsValueEnumerable().Where(includedSet.Contains).ToList()
        };
    }

    private static MaterializedNode Materialize(
        PromptNode source,
        MaterializedContainer? parent,
        bool escapeText,
        int inheritedBudget,
        List<PromptTextChunk> truncated,
        List<MaterializedTokenLimit> localLimits)
    {
        switch (source)
        {
            case PromptText text:
                return new MaterializedText(source, parent, EncodeText(text.Text, escapeText));
            case PromptTextChunk chunk:
            {
                var encoded = EncodeText(chunk.Text, escapeText);
                var localBudget = Math.Min(inheritedBudget, chunk.MaxTokens ?? inheritedBudget);
                var shortened = ShortenChunk(chunk, escapeText, localBudget);
                if (!shortened.Equals(encoded, StringComparison.Ordinal)) truncated.Add(chunk);
                return new MaterializedText(source, parent, shortened);
            }
            case PromptContainer container:
            {
                var materialized = new MaterializedContainer(source, parent);
                var childEscape = escapeText || source is PromptElement;
                var childBudget = source is PromptTokenLimit limit
                    ? Math.Min(inheritedBudget, limit.MaxTokens)
                    : inheritedBudget;
                foreach (var child in container.Children)
                {
                    materialized.Children.Add(
                        Materialize(child, materialized, childEscape, childBudget, truncated, localLimits));
                }

                // Post-order collection makes nested limits deterministic without assigning public node IDs.
                if (source is PromptTokenLimit tokenLimit)
                {
                    localLimits.Add(new MaterializedTokenLimit(materialized, tokenLimit.MaxTokens));
                }

                return materialized;
            }
            default:
                throw new NotSupportedException($"Unsupported prompt node type '{source.GetType().FullName}'.");
        }
    }

    private static string ShortenChunk(PromptTextChunk chunk, bool escapeText, int tokenBudget)
    {
        var encoded = EncodeText(chunk.Text, escapeText);
        if (chunk.BreakMode == PromptTextBreakMode.None ||
            TokenHelper.EstimateTokenCount(encoded) <= tokenBudget)
        {
            return encoded;
        }

        if (tokenBudget <= 0) return string.Empty;
        var endpoints = GetBreakEndpoints(chunk).Distinct().Order().ToList();
        var low = 0;
        var high = endpoints.Count - 1;
        var best = 0;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var endpoint = endpoints[middle];
            var candidate = EncodeText(chunk.Text[..endpoint], escapeText);
            if (TokenHelper.EstimateTokenCount(candidate) <= tokenBudget)
            {
                best = endpoint;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return EncodeText(chunk.Text[..best], escapeText);
    }

    private static IEnumerable<int> GetBreakEndpoints(PromptTextChunk chunk)
    {
        switch (chunk.BreakMode)
        {
            case PromptTextBreakMode.Whitespace:
                for (var i = 0; i < chunk.Text.Length; i++)
                {
                    if (!char.IsWhiteSpace(chunk.Text[i])) continue;
                    while (i + 1 < chunk.Text.Length && char.IsWhiteSpace(chunk.Text[i + 1])) i++;
                    yield return i + 1;
                }
                break;
            case PromptTextBreakMode.Line:
                for (var i = 0; i < chunk.Text.Length; i++)
                {
                    if (chunk.Text[i] == '\n' || chunk.Text[i] == '\r' && (i + 1 == chunk.Text.Length || chunk.Text[i + 1] != '\n'))
                        yield return i + 1;
                }
                break;
            case PromptTextBreakMode.Separator:
            {
                var separator = chunk.Separator;
                if (string.IsNullOrEmpty(separator))
                {
                    throw new InvalidOperationException("A separator text chunk requires a non-empty separator.");
                }

                var start = 0;
                while (start < chunk.Text.Length)
                {
                    var index = chunk.Text.IndexOf(separator, start, StringComparison.Ordinal);
                    if (index < 0) break;
                    start = index + separator.Length;
                    yield return start;
                }
                break;
            }
        }

        yield return chunk.Text.Length;
    }

    private static MaterializedNode? FindLowestPriorityNode(MaterializedContainer container)
    {
        MaterializedNode? lowest = null;
        foreach (var candidate in EnumeratePriorityChildren(container))
        {
            if (candidate.IsEmpty) continue;
            if (lowest is null || candidate.Priority < lowest.Priority)
            {
                lowest = candidate;
                continue;
            }

            if (candidate.Priority != lowest.Priority) continue;

            // Equal-priority containers are compared by their next removable descendant. This keeps a
            // high-priority wrapper from hiding cheaper nested content while preserving declaration order
            // when both subtrees are otherwise equivalent.
            if (GetLowestNestedPriority(candidate) < GetLowestNestedPriority(lowest)) lowest = candidate;
        }

        if (lowest is not MaterializedContainer nested || nested.IsAtomic || nested.IsEmpty) return lowest;
        return FindLowestPriorityNode(nested) ?? lowest;
    }

    private static IEnumerable<MaterializedNode> EnumeratePriorityChildren(MaterializedContainer container)
    {
        foreach (var child in container.Children)
        {
            if (child is MaterializedContainer { PassPriority: true } transparent)
            {
                foreach (var descendant in EnumeratePriorityChildren(transparent)) yield return descendant;
            }
            else
            {
                yield return child;
            }
        }
    }

    private static int GetLowestNestedPriority(MaterializedNode node)
    {
        if (node is not MaterializedContainer container || container.IsAtomic) return int.MaxValue;
        return EnumeratePriorityChildren(container).AsValueEnumerable().Where(c => !c.IsEmpty).Select(c => c.Priority).Prepend(int.MaxValue).Min();
    }

    private static string EncodeText(string text, bool escapeText) => escapeText ? SecurityElement.Escape(text) : text;

    private readonly record struct MaterializedTokenLimit(MaterializedContainer Container, int MaxTokens);

    private static IEnumerable<PromptNode> EnumerateSourceNodes(PromptNode node)
    {
        yield return node;
        if (node is not PromptContainer container) yield break;
        foreach (var child in container.Children)
        {
            foreach (var descendant in EnumerateSourceNodes(child)) yield return descendant;
        }
    }

    private static void CollectIncluded(MaterializedNode node, HashSet<PromptNode> included)
    {
        if (node.IsEmpty) return;
        included.Add(node.Source);
        if (node is not MaterializedContainer container) return;
        foreach (var child in container.Children) CollectIncluded(child, included);
    }

    /// <summary>
    /// Represents one node in the disposable render tree built from a declarative source node.
    /// </summary>
    private abstract class MaterializedNode(PromptNode source, MaterializedContainer? parent)
    {
        public PromptNode Source { get; } = source;

        public MaterializedContainer? Parent { get; } = parent;

        public int Priority => Source.Priority;

        public abstract bool IsEmpty { get; }

        public abstract int UpperBoundTokenCount { get; }

        public abstract string Render();

        public void Remove()
        {
            if (Parent is null) return;
            Parent.Children.Remove(this);
            Parent.Invalidate();
        }
    }

    /// <summary>
    /// Carries already escaped and optionally shortened text in the disposable render tree.
    /// </summary>
    private sealed class MaterializedText(PromptNode source, MaterializedContainer? parent, string text) : MaterializedNode(source, parent)
    {
        public override bool IsEmpty => text.Length == 0;

        public override int UpperBoundTokenCount => TokenHelper.EstimateTokenCount(text);

        public override string Render() => text;
    }

    /// <summary>
    /// Carries removable child nodes and render caches without mutating the public declaration tree.
    /// </summary>
    private sealed class MaterializedContainer(PromptNode source, MaterializedContainer? parent) : MaterializedNode(source, parent)
    {
        public List<MaterializedNode> Children { get; } = [];

        public bool PassPriority => Source is PromptContainer { PassPriority: true };

        public bool IsAtomic => Source is PromptChunk;

        public override bool IsEmpty => Children.All(static child => child.IsEmpty);

        public override int UpperBoundTokenCount => _upperBound ??= CalculateUpperBound();

        private string? _rendered;
        private int? _upperBound;

        public override string Render()
        {
            if (_rendered is not null) return _rendered;
            if (IsEmpty) return _rendered = string.Empty;

            var builder = new StringBuilder();
            if (Source is PromptElement element)
            {
                builder.Append('<').Append(element.Name);
                foreach (var (name, value) in element.Attributes.AsValueEnumerable())
                {
                    builder.Append(' ').Append(name).Append("=\"").Append(SecurityElement.Escape(value)).Append('\"');
                }

                builder.Append('>');
            }

            foreach (var child in Children.AsValueEnumerable().Where(child => !child.IsEmpty))
            {
                builder.Append(child.Render());
            }

            if (Source is PromptElement closingElement)
            {
                builder.Append("</").Append(closingElement.Name).Append('>');
            }

            return _rendered = builder.ToString();
        }

        public void Invalidate()
        {
            _rendered = null;
            _upperBound = null;
            Parent?.Invalidate();
        }

        private int CalculateUpperBound()
        {
            if (IsEmpty) return 0;
            var total = Children.Sum(static child => child.UpperBoundTokenCount);
            if (Source is not PromptElement element) return total;

            var opening = new StringBuilder().Append('<').Append(element.Name);
            foreach (var (name, value) in element.Attributes.AsValueEnumerable())
            {
                opening.Append(' ').Append(name).Append("=\"").Append(SecurityElement.Escape(value)).Append('\"');
            }

            opening.Append('>');
            total += TokenHelper.EstimateTokenCount(opening.ToString());
            total += TokenHelper.EstimateTokenCount($"</{element.Name}>");
            return total;
        }
    }
}

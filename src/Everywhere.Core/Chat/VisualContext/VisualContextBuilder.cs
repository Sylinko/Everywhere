#if DEBUG
#define DEBUG_VISUAL_TREE_BUILDER
#endif

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Automation;
using Everywhere.Views;

namespace Everywhere.Chat;

/// <summary>
///     This class builds an XML representation of the core elements, which is limited by the soft token limit and finally used by a LLM.
/// </summary>
/// <param name="coreElements"></param>
/// <param name="approximateTokenLimit"></param>
/// <param name="detailLevel"></param>
public sealed partial class VisualContextBuilder(
    IReadOnlyList<VisualElement> coreElements,
    VisualElementRetention retention,
    VisualTargetPublicationBatch targetPublication,
    int approximateTokenLimit,
    VisualContextDetailLevel detailLevel,
    VisualContextTraverseDirections allowedTraverseDirections = VisualContextTraverseDirections.All,
    VisualElementEffect.ScanEffectScope? effectScope = null
)
{
    /// <summary>
    /// Represents a node in the XML tree being built.
    /// This class is mutable to support dynamic updates of activation state during traversal.
    /// </summary>
    private class VisualElementNode(
        VisualElementQueryResult queryResult,
        string? pendingParentAnchorId,
        int siblingIndex,
        string? description,
        IReadOnlyList<string> contentLines,
        int tokenCount,
        int contentTokenCount,
        bool isSelfInformative,
        bool isImportant
    )
    {
        public VisualElement Element => QueryResult.Element;

        public VisualElementQueryResult QueryResult { get; } = queryResult;

        public VisualElementSnapshot Snapshot => QueryResult.Snapshot;

        public VisualElementType Type => Snapshot.Type ?? VisualElementType.Unknown;

        public string? PendingParentAnchorId { get; set; } = pendingParentAnchorId;

        public int SiblingIndex { get; } = siblingIndex;

        public string? Description { get; } = description;

        public IReadOnlyList<string> ContentLines { get; } = contentLines;

        /// <summary>
        /// The token cost of the element's structure (tags, attributes, ID) excluding content text.
        /// </summary>
        public int TokenCount { get; } = tokenCount;

        /// <summary>
        /// The token cost of the element's content text (Description, Contents).
        /// </summary>
        public int ContentTokenCount { get; } = contentTokenCount;

        public VisualElementNode? Parent { get; set; }

        public HashSet<VisualElementNode> Children { get; } = [];

        /// <summary>
        /// Indicates whether this element should be rendered in the final XML.
        /// This is determined dynamically based on <see cref="VisualContextDetailLevel"/> and the presence of informative children.
        /// </summary>
        public bool IsVisible { get; set; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is intrinsically informative (e.g., has text, is interactive, or is a core element).
        /// If true, <see cref="IsVisible"/> is always true.
        /// </summary>
        public bool IsSelfInformative { get; } = isSelfInformative;

        /// <summary>
        /// Indicates whether this element is an important element.
        /// </summary>
        public bool IsImportant { get; } = isImportant;

        /// <summary>
        /// The number of children that have informative content (either self-informative or have informative descendants).
        /// </summary>
        public int InformativeChildCount { get; set; }

        /// <summary>
        /// Indicates whether this element has any informative descendants.
        /// </summary>
        public bool HasInformativeDescendants { get; set; }

        /// <summary>
        /// Indicates that some children of this element were omitted due to the token budget being exhausted.
        /// Set during the BFS cleanup phase when remaining queue items are discarded.
        /// </summary>
        public bool HasOmittedChildren { get; set; }

        /// <summary>
        /// Indicates that the text content of this element was truncated to fit the remaining token budget.
        /// </summary>
        public bool IsContentOmitted { get; set; }
    }

    /// <summary>
    /// Hierarchical DTO for JSON / TOON serialization.
    /// Property names are deliberately short to minimise token usage.
    /// Null fields are omitted by <see cref="CompactJsonOptions"/>.
    /// </summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private readonly record struct VisualElementDto(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("type"), JsonConverter(typeof(JsonStringEnumConverter))] VisualElementType Type,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("box")] string? Box,
        [property: JsonPropertyName("extra")] string? Extra,
        [property: JsonPropertyName("children")] List<VisualElementDto>? Children,
        [property: JsonPropertyName("omitted")] string? Omitted,
        [property: JsonIgnore] VisualElement? Target
    );

    /// <summary>
    ///     The mapping from Agent target ID to the visual element represented by the final output.
    /// </summary>
    public Dictionary<int, VisualElement> BuiltVisualElements { get; } = [];

    private readonly HashSet<string> _coreElementIdSet = coreElements
        .AsValueEnumerable()
        .Select(e => e.Id)
        .Where(id => !string.IsNullOrEmpty(id))
        .ToHashSet(StringComparer.Ordinal);

    private readonly VisualElementQueryRequest _queryRequest = new(
        VisualElementFields.All,
        approximateTokenLimit == int.MaxValue ? 65_536 : Math.Clamp(approximateTokenLimit * 4, 4_096, 65_536));

    private string? _cachedResult;

#if DEBUG_VISUAL_TREE_BUILDER
    private VisualContextRecorder? _debugRecorder;
#endif

    private static readonly JsonSerializerOptions CompactJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private const VisualElementStates InteractiveStates = VisualElementStates.Focused | VisualElementStates.Selected;

    /// <summary>
    /// Builds the text representation of the visual tree for the core elements.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public string Build(CancellationToken cancellationToken)
    {
        if (coreElements.Count == 0) throw new InvalidOperationException("No core elements to build.");

        if (_cachedResult != null) return _cachedResult;
        cancellationToken.ThrowIfCancellationRequested();

#if DEBUG_VISUAL_TREE_BUILDER
        _debugRecorder = new VisualContextRecorder(coreElements, approximateTokenLimit, "WeightedPriority");
#endif

        // Priority Queue for Best-First Search
        var priorityQueue = new PriorityQueue<TraversalNode, float>();
        var visitedElements = new Dictionary<string, VisualElementNode>();

        // 1. Enqueue core nodes
        TryEnqueueTraversalNode(priorityQueue, null, 0, VisualContextTraverseDirections.Core, new CoreElementEnumerator(coreElements, _queryRequest));

        // 2. Process the Queue
        ProcessTraversalQueue(priorityQueue, visitedElements, cancellationToken);

        // 3. Dispose remaining enumerators and mark omitted parents.
        // Any node still in the queue was discarded due to token budget exhaustion.
        // If its parent was already visited, that parent has omitted children.
        while (priorityQueue.Count > 0)
        {
            if (priorityQueue.TryDequeue(out var node, out _))
            {
                if (node.DirectParentId is not null && visitedElements.TryGetValue(node.DirectParentId, out var parentNode))
                {
                    parentNode.HasOmittedChildren = true;
                }

                node.Enumerator.Dispose();
            }
        }

        // 4. Generate output based on detail level
        _cachedResult = detailLevel switch
        {
            VisualContextDetailLevel.Detailed => GenerateXmlString(visitedElements),
            VisualContextDetailLevel.Compact => GenerateJsonString(visitedElements),
            _ => GenerateToonString(visitedElements),
        };

#if DEBUG_VISUAL_TREE_BUILDER
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var filename = $"visual_tree_debug_{timestamp}.json";
        var debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, filename);
        _debugRecorder?.SaveSession(debugPath);
#endif

        return _cachedResult;
    }

    /// <summary>
    /// Generates a compact minified JSON string from the visual tree using <see cref="VisualElementDto"/>.
    /// The output preserves the full tree hierarchy via nested <c>ch</c> (children) arrays.
    /// Null fields are omitted to minimize token usage.
    /// </summary>
    private string GenerateJsonString(Dictionary<string, VisualElementNode> visitedElements)
    {
        var tree = AssignDtoTargetIds(BuildElementDtoTree(visitedElements));
        return JsonSerializer.Serialize(tree, CompactJsonOptions);
    }

    /// <summary>
    /// Generates a TOON (Token-Oriented Object Notation) string from the visual tree.
    /// The output preserves the full tree hierarchy.
    /// </summary>
    private string GenerateToonString(Dictionary<string, VisualElementNode> visitedElements)
    {
        var tree = AssignDtoTargetIds(BuildElementDtoTree(visitedElements));

        var sb = new StringBuilder("{id,type,name,text,box,extra,children,omitted}[");
        sb.Append(tree.Count).Append(']').AppendLine();

        foreach (var root in tree)
        {
            EncodeToonString(sb, root, 0);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Encodes a single <see cref="VisualElementDto"/> and its children into TOON format.
    /// </summary>
    /// <example>
    /// 12|Label|"System.Net.Http.HttpRequestException: Response status code does not indicate success: 500 (Internal Server Error). — sylinko — everywhere - 内存使用率 - 393 MB"||||0
    /// </example>
    /// <param name="sb"></param>
    /// <param name="dto"></param>
    /// <param name="indentLevel"></param>
    private static void EncodeToonString(StringBuilder sb, VisualElementDto dto, int indentLevel)
    {
        if (indentLevel > 0) sb.Append(new string(' ', indentLevel * 2));

        sb.Append(dto.Id).Append('|').Append(dto.Type).Append('|');
        if (!string.IsNullOrEmpty(dto.Name)) sb.Append(JsonSerializer.Serialize(dto.Name, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Text)) sb.Append(JsonSerializer.Serialize(dto.Text, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Box)) sb.Append(JsonSerializer.Serialize(dto.Box, CompactJsonOptions));
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Extra)) sb.Append(JsonSerializer.Serialize(dto.Extra, CompactJsonOptions));
        sb.Append("|[").Append(dto.Children?.Count ?? 0).Append(']');
        sb.Append('|');
        if (!string.IsNullOrEmpty(dto.Omitted)) sb.Append(JsonSerializer.Serialize(dto.Omitted, CompactJsonOptions));
        sb.AppendLine();

        if (dto.Children is { Count: > 0 } children)
        {
            foreach (var child in children)
            {
                EncodeToonString(sb, child, indentLevel + 1);
            }
        }
    }

    /// <summary>
    /// Builds a hierarchical list of root <see cref="VisualElementDto"/> trees from the visited elements.
    /// Non-visible containers are skipped (passthrough) — their children are promoted to the parent level,
    /// replicating the same structural semantics as <see cref="BuildXml"/>.
    /// Synthetic TopLevel/Screen roots are created when the actual root is a non-top-level element.
    /// </summary>
    private List<VisualElementDto> BuildElementDtoTree(Dictionary<string, VisualElementNode> visitedElements)
    {
        var roots = new List<VisualElementDto>();
        foreach (var rootElement in visitedElements.Values.AsValueEnumerable().Where(e => e.Parent is null))
        {
            CollectVisibleDtos(roots, rootElement);
        }

        return MergeConsecutiveLabels(roots);
    }

    /// <summary>
    /// Recursively builds <see cref="VisualElementDto"/> nodes for the tree.
    /// Visible elements produce a DTO whose <see cref="VisualElementDto.Children"/> contains
    /// their own visible descendants. Non-visible containers are transparent — their children
    /// are promoted directly into <paramref name="output"/> (passthrough semantics).
    /// </summary>
    private void CollectVisibleDtos(List<VisualElementDto> output, VisualElementNode elementNode)
    {
        var element = elementNode.Element;
        var elementType = elementNode.Type;

        // Non-visible non-top-level elements pass through: skip self, promote children.
        if (!elementNode.IsVisible && elementType is not VisualElementType.TopLevel and not VisualElementType.Screen)
        {
            foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
            {
                CollectVisibleDtos(output, child);
            }

            return;
        }

        var childDtos = new List<VisualElementDto>();
        foreach (var child in elementNode.Children.AsValueEnumerable().OrderBy(x => x.SiblingIndex))
        {
            CollectVisibleDtos(childDtos, child);
        }

        childDtos = MergeConsecutiveLabels(childDtos);

        // Compute omission marker
        var omitted = GetOmittedMarker(elementNode.HasOmittedChildren, elementNode.IsContentOmitted);

        output.Add(
            CreateElementDto(
                elementNode,
                0,
                elementNode.Description,
                elementNode.ContentLines,
                elementNode.IsImportant,
                children: childDtos.Count > 0 ? childDtos : null,
                omitted: omitted));
    }

    /// <summary>
    /// Merges runs of consecutive childless <see cref="VisualElementType.Label"/> DTOs into
    /// a single DTO to reduce token waste. The merged element keeps the first label's ID,
    /// concatenates names and texts, unions bounding boxes, and combines extras.
    /// </summary>
    private static List<VisualElementDto> MergeConsecutiveLabels(List<VisualElementDto> dtos)
    {
        if (dtos.Count < 2) return dtos;

        var result = new List<VisualElementDto>(dtos.Count);
        var i = 0;

        while (i < dtos.Count)
        {
            var current = dtos[i];
            if (current.Type != VisualElementType.Label || current.Children is { Count: > 0 })
            {
                result.Add(current);
                i++;
                continue;
            }

            // Scan for the end of the consecutive-label run.
            var j = i + 1;
            while (j < dtos.Count && dtos[j].Type == VisualElementType.Label && dtos[j].Children is null or { Count: 0 })
            {
                j++;
            }

            if (j - i == 1)
            {
                // Single label — no merging needed.
                result.Add(current);
                i++;
                continue;
            }

            result.Add(MergeLabelRange(dtos, i, j));
            i = j;
        }

        return result;
    }

    /// <summary>
    /// Produces a single merged <see cref="VisualElementDto"/> from the label DTOs in
    /// <paramref name="dtos"/>[<paramref name="start"/> .. <paramref name="end"/>).
    /// </summary>
    private static VisualElementDto MergeLabelRange(List<VisualElementDto> dtos, int start, int end)
    {
        var first = dtos[start];

        StringBuilder? nameBuilder = null;
        StringBuilder? textBuilder = null;
        StringBuilder? extraBuilder = null;
        StringBuilder? omittedBuilder = null;

        int? minX = null, minY = null, maxX2 = null, maxY2 = null;

        for (var k = start; k < end; k++)
        {
            var dto = dtos[k];

            if (dto.Name is { Length: > 0 } name)
            {
                nameBuilder ??= new StringBuilder();
                if (nameBuilder.Length > 0) nameBuilder.Append(' ');
                nameBuilder.Append(name);
            }

            if (dto.Text is { Length: > 0 } text)
            {
                textBuilder ??= new StringBuilder();
                if (textBuilder.Length > 0) textBuilder.Append(' ');
                textBuilder.Append(text);
            }

            if (dto.Extra is { Length: > 0 } extra)
            {
                extraBuilder ??= new StringBuilder();
                if (extraBuilder.Length > 0) extraBuilder.Append(',');
                extraBuilder.Append(extra);
            }

            // Merge omitted markers from individual labels (union of all flags)
            if (dto.Omitted is { Length: > 0 } omitted)
            {
                if (omittedBuilder is null)
                {
                    omittedBuilder = new StringBuilder(omitted);
                }
                else
                {
                    foreach (var part in omitted.Split(','))
                    {
                        if (!omittedBuilder.ToString().Contains(part, StringComparison.Ordinal))
                        {
                            omittedBuilder.Append(',').Append(part);
                        }
                    }
                }
            }

            if (dto.Box is not null)
            {
                var parts = dto.Box.Split(',');
                if (parts.Length == 4
                    && int.TryParse(parts[0], out var x)
                    && int.TryParse(parts[1], out var y)
                    && int.TryParse(parts[2], out var w)
                    && int.TryParse(parts[3], out var h))
                {
                    var x2 = x + w;
                    var y2 = y + h;
                    minX = minX is null ? x : Math.Min(minX.Value, x);
                    minY = minY is null ? y : Math.Min(minY.Value, y);
                    maxX2 = maxX2 is null ? x2 : Math.Max(maxX2.Value, x2);
                    maxY2 = maxY2 is null ? y2 : Math.Max(maxY2.Value, y2);
                }
            }
        }

        return new VisualElementDto
        {
            Id = first.Id,
            Type = first.Type,
            Name = nameBuilder?.ToString(),
            Text = textBuilder?.ToString(),
            Box = minX is not null ? $"{minX},{minY},{maxX2!.Value - minX.Value},{maxY2!.Value - minY!.Value}" : null,
            Extra = extraBuilder?.ToString(),
            Children = null,
            Omitted = omittedBuilder?.ToString(),
            Target = first.Target
        };
    }

    /// <summary>
    /// Creates a single <see cref="VisualElementDto"/> for the given visual element.
    /// Secondary metadata (importance flag, TopLevel process info, window handle)
    /// is assembled into the compact <see cref="VisualElementDto.Extra"/> string.
    /// </summary>
    private VisualElementDto CreateElementDto(
        VisualElementNode elementNode,
        int id,
        string? description,
        IReadOnlyList<string>? contentLines,
        bool isImportant,
        List<VisualElementDto>? children,
        string? omitted = null)
    {
        var snapshot = elementNode.Snapshot;
        var elementType = elementNode.Type;

        // Build Box
        string? box = null;
        if (ShouldIncludeBounds(detailLevel, elementType) && snapshot.Bounds is { } bounds)
        {
            box = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";
        }

        // Build Extra — assemble all secondary metadata into a compact string
        var extraPartsBuilder = new StringBuilder();
        if (isImportant) extraPartsBuilder.Append("!important");
        if (elementType == VisualElementType.TopLevel)
        {
            var processId = snapshot.ProcessId.GetValueOrDefault(-1);
            if (processId > 0)
            {
                AppendExtraPart("pid:").Append(processId);
                try
                {
                    using var process = Process.GetProcessById(processId);
                    AppendExtraPart("process:").Append(process.ProcessName);
                }
                catch
                {
                    // Ignore if process not found
                }
            }

            var windowHandle = snapshot.NativeWindowHandle.GetValueOrDefault();
            if (windowHandle > 0) AppendExtraPart("hwnd:0x").Append(windowHandle.ToString("X"));
        }

        return new VisualElementDto(
            id,
            elementType,
            description,
            contentLines is { Count: > 0 } ? string.Join('\n', contentLines) : null,
            box,
            extraPartsBuilder.Length > 0 ? extraPartsBuilder.ToString() : null,
            children,
            omitted,
            elementNode.Element);

        StringBuilder AppendExtraPart(string part)
        {
            if (extraPartsBuilder.Length > 0) extraPartsBuilder.Append(',');
            return extraPartsBuilder.Append(part);
        }
    }

    private List<VisualElementDto> AssignDtoTargetIds(List<VisualElementDto> dtos)
    {
        for (var i = 0; i < dtos.Count; i++)
        {
            var dto = dtos[i];
            var children = dto.Children is null ? null : AssignDtoTargetIds(dto.Children);
            var id = dto.Target is null ? 0 : AddTarget(dto.Target);
            dtos[i] = dto with { Id = id, Children = children };
        }

        return dtos;
    }

    private int AddTarget(VisualElement element)
    {
        var id = targetPublication.Add(new ElementTarget { Element = element });
        BuiltVisualElements[id] = element;
        return id;
    }
}

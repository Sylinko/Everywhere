#if DEBUG
#define DEBUG_VISUAL_TREE_BUILDER
#endif

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Interop;
using Everywhere.Views;
using ZLinq;

namespace Everywhere.Chat;

/// <summary>
///     This class builds an XML representation of the core elements, which is limited by the soft token limit and finally used by a LLM.
/// </summary>
/// <param name="coreElements"></param>
/// <param name="approximateTokenLimit"></param>
/// <param name="detailLevel"></param>
public sealed partial class VisualContextBuilder(
    IReadOnlyList<IVisualElement> coreElements,
    int approximateTokenLimit,
    int startingId,
    VisualContextDetailLevel detailLevel,
    VisualContextTraverseDirections allowedTraverseDirections = VisualContextTraverseDirections.All,
    VisualElementEffect.ScanEffectScope? effectScope = null
)
{
    private static readonly ActivitySource ActivitySource = new(typeof(VisualContextBuilder).FullName.NotNull());

    /// <summary>
    /// Builds the text representation of the visual tree for the given attachments as core elements and populates the attachment contents.
    /// </summary>
    /// <param name="attachments"></param>
    /// <param name="approximateTokenLimit"></param>
    /// <param name="startingId"></param>
    /// <param name="detailLevel"></param>
    /// <param name="effectScope"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public static IReadOnlyDictionary<int, IVisualElement> BuildAndPopulate(
        IReadOnlyList<VisualElementAttachment> attachments,
        int approximateTokenLimit,
        int startingId,
        VisualContextDetailLevel detailLevel,
        VisualElementEffect.ScanEffectScope? effectScope,
        CancellationToken cancellationToken)
    {
        using var builderActivity = ActivitySource.StartActivity();

        var result = new Dictionary<int, IVisualElement>();
        var validAttachments = attachments
            .AsValueEnumerable()
            .Select(x => (Attachment: x, Element: x.Element?.Target))
            .Where(t => t.Element is not null)
            .Select(t => (t.Attachment, Element: t.Element!))
            .ToList();

        if (validAttachments.Count == 0)
        {
            return result;
        }

        // 1. Group core elements by their root element. Key is tuple (ProcessId, NativeWindowHandle of the ancestor TopLevel)
        var groups = validAttachments
            .AsValueEnumerable()
            .GroupBy(x =>
            {
                var current = x.Element;
                while (current is { Type: not VisualElementType.Screen and not VisualElementType.TopLevel, Parent: { } parent })
                {
                    current = parent;
                }

                return (x.Element.ProcessId, current.NativeWindowHandle);
            })
            .ToArray();

        var totalElements = validAttachments.Count;
        var totalBuiltElements = 0;

        foreach (var group in groups.AsValueEnumerable())
        {
            var groupElements = group.AsValueEnumerable().Select(x => x.Element).ToList();
            var groupCount = groupElements.Count;

            // 2. Build XML for each root group
            // Allocate token limit relative to the number of elements in the group
            var groupTokenLimit = (int)((long)approximateTokenLimit * groupCount / totalElements);

            var visualTreeBuilder = new VisualContextBuilder(
                groupElements,
                groupTokenLimit,
                startingId,
                detailLevel,
                effectScope: effectScope);

            var content = visualTreeBuilder.Build(cancellationToken);

            // 3. for attachments in the same group
            // First attachment gets the full XML, others got null.
            var isFirst = true;
            foreach (var (attachment, _) in group.AsValueEnumerable())
            {
                if (isFirst)
                {
                    attachment.Content = content;
                    isFirst = false;
                }
                else
                {
                    attachment.Content = null;
                }
            }

            foreach (var kvp in visualTreeBuilder.BuiltVisualElements.AsValueEnumerable())
            {
                result[kvp.Key] = kvp.Value;
            }

            startingId += visualTreeBuilder.BuiltVisualElements.Count;
            totalBuiltElements += visualTreeBuilder.BuiltVisualElements.Count;
        }

        builderActivity?.SetTag("xml.detail_level", detailLevel);
        builderActivity?.SetTag("xml.length_limit", approximateTokenLimit);
        builderActivity?.SetTag("xml.built_visual_elements.count", totalBuiltElements);

        return result;
    }

    /// <summary>
    /// Represents a node in the XML tree being built.
    /// This class is mutable to support dynamic updates of activation state during traversal.
    /// </summary>
    private class VisualElementNode(
        IVisualElement element,
        VisualElementType type,
        string? parentId,
        int siblingIndex,
        string? description,
        IReadOnlyList<string> contentLines,
        int tokenCount,
        int contentTokenCount,
        bool isSelfInformative,
        bool isImportant
    )
    {
        public IVisualElement Element { get; } = element;

        public VisualElementType Type { get; } = type;

        public string? ParentId { get; } = parentId;

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
        [property: JsonPropertyName("omitted")] string? Omitted
    );

    /// <summary>
    ///     The mapping from original element ID to the built sequential ID starting from <see cref="startingId"/>.
    /// </summary>
    public Dictionary<int, IVisualElement> BuiltVisualElements { get; } = [];

    private readonly HashSet<string> _coreElementIdSet = coreElements
        .Select(e => e.Id)
        .Where(id => !string.IsNullOrEmpty(id))
        .ToHashSet(StringComparer.Ordinal);

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
        TryEnqueueTraversalNode(priorityQueue, null, 0, VisualContextTraverseDirections.Core, coreElements.GetEnumerator());

        // 2. Process the Queue
        ProcessTraversalQueue(priorityQueue, visitedElements, cancellationToken);

        // 3. Dispose remaining enumerators and mark omitted parents.
        // Any node still in the queue was discarded due to token budget exhaustion.
        // If its parent was already visited, that parent has omitted children.
        while (priorityQueue.Count > 0)
        {
            if (priorityQueue.TryDequeue(out var node, out _))
            {
                if (node.ParentId is not null && visitedElements.TryGetValue(node.ParentId, out var parentNode))
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
        var tree = BuildElementDtoTree(visitedElements);
        return JsonSerializer.Serialize(tree, CompactJsonOptions);
    }

    /// <summary>
    /// Generates a TOON (Token-Oriented Object Notation) string from the visual tree.
    /// The output preserves the full tree hierarchy.
    /// </summary>
    private string GenerateToonString(Dictionary<string, VisualElementNode> visitedElements)
    {
        var tree = BuildElementDtoTree(visitedElements);

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
            if (rootElement.Type is not VisualElementType.TopLevel and not VisualElementType.Screen)
            {
                // Walk up to find the actual TopLevel/Screen ancestor
                var topLevelOrScreenElement = rootElement.Element.Parent;
                while (topLevelOrScreenElement is { Type: not VisualElementType.TopLevel and not VisualElementType.Screen, Parent: { } parent })
                {
                    topLevelOrScreenElement = parent;
                }

                if (topLevelOrScreenElement is not null)
                {
                    var syntheticId = BuiltVisualElements.Count + startingId;
                    BuiltVisualElements[syntheticId] = topLevelOrScreenElement;

                    // Collect children from the rootElement subtree
                    var childDtos = new List<VisualElementDto>();
                    CollectVisibleDtos(childDtos, rootElement);
                    childDtos = MergeConsecutiveLabels(childDtos);

                    roots.Add(
                        CreateElementDto(
                            topLevelOrScreenElement,
                            topLevelOrScreenElement.Type,
                            syntheticId,
                            description: null,
                            contentLines: null,
                            isImportant: false,
                            children: childDtos.Count > 0 ? childDtos : null,
                            omitted: "children")); // Synthetic roots always have omitted children
                    continue;
                }
            }

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

        // Visible node: assign sequential ID and recurse children.
        var id = BuiltVisualElements.Count + startingId;
        BuiltVisualElements[id] = element;

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
                element,
                elementType,
                id,
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
            Omitted = omittedBuilder?.ToString()
        };
    }

    /// <summary>
    /// Creates a single <see cref="VisualElementDto"/> for the given visual element.
    /// Secondary metadata (importance flag, TopLevel process info, window handle)
    /// is assembled into the compact <see cref="VisualElementDto.Extra"/> string.
    /// </summary>
    private VisualElementDto CreateElementDto(
        IVisualElement element,
        VisualElementType elementType,
        int id,
        string? description,
        IReadOnlyList<string>? contentLines,
        bool isImportant,
        List<VisualElementDto>? children,
        string? omitted = null)
    {
        // Build Box
        string? box = null;
        if (ShouldIncludeBounds(detailLevel, elementType))
        {
            var bounds = element.BoundingRectangle;
            box = $"{bounds.X},{bounds.Y},{bounds.Width},{bounds.Height}";
        }

        // Build Extra — assemble all secondary metadata into a compact string
        var extraPartsBuilder = new StringBuilder();
        if (isImportant) extraPartsBuilder.Append("!important");
        if (elementType == VisualElementType.TopLevel)
        {
            var processId = element.ProcessId;
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

            var windowHandle = element.NativeWindowHandle;
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
            omitted);

        StringBuilder AppendExtraPart(string part)
        {
            if (extraPartsBuilder.Length > 0) extraPartsBuilder.Append(',');
            return extraPartsBuilder.Append(part);
        }
    }
}
using System.Collections;
using MessagePack;

namespace Everywhere.Prompting.Documents;

/// <summary>
/// Represents one declarative node in a model-facing prompt document.
/// </summary>
/// <remarks>
/// Priorities are compared within the nearest non-transparent container. Nodes without an explicit
/// priority are treated as required content and therefore default to the highest priority.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(PromptText))]
[Union(1, typeof(PromptTextChunk))]
[Union(2, typeof(PromptDocument))]
[Union(3, typeof(PromptGroup))]
[Union(4, typeof(PromptElement))]
[Union(5, typeof(PromptChunk))]
[Union(6, typeof(PromptTokenLimit))]
[Union(7, typeof(PromptCompactElement))]
public abstract partial class PromptNode
{
    /// <summary>
    /// Gets or sets the relative importance of this node within its priority scope.
    /// </summary>
    [Key(0)]
    public int Priority { get; set; } = int.MaxValue;

    /// <summary>
    /// Converts ordinary text into an atomic prompt text node.
    /// </summary>
    public static implicit operator PromptNode(string text) => new PromptText(text);

    /// <summary>
    /// Renders this node without a document-wide token budget while applying any local limits.
    /// </summary>
    public override string ToString() => PromptNodeRenderer.Render(this);
}

/// <summary>
/// Represents an ordered prompt node that is either kept in full or removed in full.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptText : PromptNode
{
    /// <summary>
    /// Gets the text carried by this node.
    /// </summary>
    [Key(1)]
    public string Text { get; private set; } = string.Empty;

    [SerializationConstructor]
    private PromptText() { }

    /// <summary>
    /// Creates an atomic text node.
    /// </summary>
    public PromptText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        Text = text;
    }
}

/// <summary>
/// Defines the safe boundaries at which a <see cref="PromptTextChunk"/> may be shortened.
/// </summary>
public enum PromptTextBreakMode
{
    /// <summary>
    /// The text is atomic and cannot be shortened.
    /// </summary>
    None,

    /// <summary>
    /// The text may be shortened at Unicode whitespace boundaries.
    /// </summary>
    Whitespace,

    /// <summary>
    /// The text may be shortened after complete logical lines.
    /// </summary>
    Line,

    /// <summary>
    /// The text may be shortened after a caller-provided separator.
    /// </summary>
    Separator
}

/// <summary>
/// Represents text that can cooperatively shorten itself at safe boundaries before priority pruning.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptTextChunk : PromptNode
{
    /// <summary>
    /// Gets the complete source text.
    /// </summary>
    [Key(1)]
    public string Text { get; private set; } = string.Empty;

    /// <summary>
    /// Gets or sets the boundary policy used when the text exceeds its budget.
    /// </summary>
    [Key(2)]
    public PromptTextBreakMode BreakMode { get; set; }

    /// <summary>
    /// Gets or sets the separator used when <see cref="BreakMode"/> is <see cref="PromptTextBreakMode.Separator"/>.
    /// </summary>
    [Key(3)]
    public string? Separator { get; set; }

    /// <summary>
    /// Gets or sets an optional local token ceiling for this text.
    /// </summary>
    [Key(4)]
    public int? MaxTokens
    {
        get;
        set
        {
            if (value.HasValue) ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value.Value, nameof(MaxTokens));
            field = value;
        }
    }

    [SerializationConstructor]
    private PromptTextChunk() { }

    /// <summary>
    /// Creates a budget-responsive text node.
    /// </summary>
    public PromptTextChunk(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>
    /// Configures a text chunk to shorten at Unicode whitespace boundaries.
    /// </summary>
    public PromptTextChunk BreakOnWhitespace()
    {
        BreakMode = PromptTextBreakMode.Whitespace;
        Separator = null;
        return this;
    }

    /// <summary>
    /// Configures a text chunk to shorten after complete lines.
    /// </summary>
    public PromptTextChunk BreakOnLines()
    {
        BreakMode = PromptTextBreakMode.Line;
        Separator = null;
        return this;
    }

    /// <summary>
    /// Configures a text chunk to shorten after a specified separator.
    /// </summary>
    public PromptTextChunk BreakOn(string separator)
    {
        ArgumentException.ThrowIfNullOrEmpty(separator);
        BreakMode = PromptTextBreakMode.Separator;
        Separator = separator;
        return this;
    }

    /// <summary>
    /// Sets a local token ceiling for a text chunk.
    /// </summary>
    public PromptTextChunk WithMaxTokens(int maxTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
        MaxTokens = maxTokens;
        return this;
    }
}

/// <summary>
/// Represents a prompt node that owns an ordered collection of child nodes.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(PromptDocument))]
[Union(1, typeof(PromptGroup))]
[Union(2, typeof(PromptElement))]
[Union(3, typeof(PromptChunk))]
[Union(4, typeof(PromptTokenLimit))]
[Union(5, typeof(PromptCompactElement))]
public abstract partial class PromptContainer(List<PromptNode> children) : PromptNode, IReadOnlyList<PromptNode>
{
    /// <summary>
    /// Gets the ordered children of this container.
    /// </summary>
    [Key(1)]
    public List<PromptNode> Children { get; } = children;

    /// <summary>
    /// Gets or sets whether children participate directly in the parent's priority scope.
    /// </summary>
    [Key(2)]
    public bool PassPriority { get; set; }

    /// <inheritdoc />
    public int Count => Children.Count;

    /// <inheritdoc />
    public PromptNode this[int index] => Children[index];

    protected PromptContainer() : this([])
    {
    }

    /// <summary>
    /// Adds a node to this container. Null nodes are ignored to support declarative conditional composition.
    /// </summary>
    public void Add(PromptNode? node)
    {
        if (node is not null) Children.Add(node);
    }

    /// <summary>
    /// Adds ordinary text to this container.
    /// </summary>
    public void Add(string? text)
    {
        if (text is not null) Children.Add(text);
    }

    /// <inheritdoc />
    public IEnumerator<PromptNode> GetEnumerator() => Children.GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// Represents the root of a declarative prompt document.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptDocument : PromptContainer
{
    public PromptDocument() { }

    [SerializationConstructor]
    private PromptDocument(List<PromptNode> children) : base(children) { }

    /// <summary>
    /// Renders this document within a token budget without modifying the declared tree.
    /// </summary>
    public PromptRenderResult Render(int maxTokenCount) => PromptNodeRenderer.Render(this, maxTokenCount);
}

/// <summary>
/// Represents a logical grouping that introduces a local priority scope without emitting markup.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptGroup : PromptContainer
{
    public PromptGroup() { }

    [SerializationConstructor]
    private PromptGroup(List<PromptNode> children) : base(children) { }
}

/// <summary>
/// Represents a prompt subtree whose rendered content must fit within a local token budget.
/// </summary>
/// <remarks>
/// The container emits no markup of its own. Its children form a local priority scope and are
/// pruned before the document-wide token budget is applied.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptTokenLimit : PromptContainer
{
    /// <summary>
    /// Gets the maximum number of tokens that may be emitted by this subtree.
    /// </summary>
    [Key(3)]
    [field: IgnoreMember]
    public int MaxTokens
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(MaxTokens));
            field = value;
        }
    }

    /// <summary>
    /// Creates a locally budgeted subtree containing the supplied children.
    /// </summary>
    public PromptTokenLimit(int maxTokens, params ReadOnlySpan<PromptNode?> children)
    {
        MaxTokens = maxTokens;
        foreach (var child in children)
        {
            if (child is not null) Children.Add(child);
        }
    }

    [SerializationConstructor]
    private PromptTokenLimit(List<PromptNode> children) : base(children) { }
}

/// <summary>
/// Represents a model-visible XML-style element.
/// </summary>
/// <remarks>
/// Elements pass priority by default so their children participate directly in the parent's priority scope.
/// An element with no surviving children is emitted as a self-closing element because its name and attributes
/// are content in their own right.
/// Wrap an element in a <see cref="PromptChunk"/> when the entire element must be kept or removed atomically.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptElement : PromptContainer
{
    /// <summary>
    /// Gets the validated XML element name.
    /// </summary>
    [Key(3)]
    public string Name { get; }

    /// <summary>
    /// Gets the ordered attributes emitted on this element.
    /// </summary>
    [Key(4)]
    public PromptAttributeCollection Attributes { get; }

    /// <summary>
    /// Creates an XML-style prompt element.
    /// </summary>
    public PromptElement(string name, params ReadOnlySpan<PromptNode?> children)
    {
        PromptXmlName.Validate(name, nameof(name));

        Name = name;
        PassPriority = true;
        foreach (var child in children)
        {
            if (child is not null) Children.Add(child);
        }
        Attributes = [];
    }

    [SerializationConstructor]
    private PromptElement(List<PromptNode> children, string name, PromptAttributeCollection attributes) : base(children)
    {
        Name = name;
        Attributes = attributes;
    }

    /// <summary>
    /// Adds or replaces an attribute after converting its value with invariant culture.
    /// </summary>
    public PromptElement Attribute(string name, object? value)
    {
        Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds or replaces an attribute after converting its value with invariant culture.
    /// Only adds the attribute if the value is not null.
    /// </summary>
    public PromptElement AttributeNotNull(string name, object? value)
    {
        if (value is not null) Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds or replaces an attribute after converting its value with invariant culture.
    /// Only adds the attribute if the value is not null and not empty string.
    /// </summary>
    public PromptElement AttributeNotNullOrEmpty(string name, object? value)
    {
        if (value is not null && value is not string { Length: 0 }) Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds a child.
    /// </summary>
    /// <param name="child"></param>
    /// <returns></returns>
    public PromptElement Child(PromptNode child)
    {
        Children.Add(child);
        return this;
    }

    /// <summary>
    /// Adds a child if the value is not null.
    /// </summary>
    /// <param name="child"></param>
    /// <returns></returns>
    public PromptElement ChildNotNull(PromptNode? child)
    {
        if (child is not null) Children.Add(child);
        return this;
    }

    /// <summary>
    /// Adds a child if the value is not null and not empty string.
    /// </summary>
    /// <param name="child"></param>
    /// <returns></returns>
    public PromptElement ChildNotNullOrEmpty(PromptNode? child)
    {
        if (child is not null && child is not PromptText { Text.Length: 0 }) Children.Add(child);
        return this;
    }
}

/// <summary>
/// Represents an atomic subtree that is kept or removed as a single unit during pruning.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptChunk : PromptContainer
{
    /// <summary>
    /// Creates an empty atomic chunk.
    /// </summary>
    public PromptChunk() { }

    /// <summary>
    /// Creates an atomic chunk containing the supplied children.
    /// </summary>
    public PromptChunk(params ReadOnlySpan<PromptNode?> children)
    {
        foreach (var child in children)
        {
            if (child is not null) Children.Add(child);
        }
    }

    [SerializationConstructor]
    private PromptChunk(List<PromptNode> children) : base(children) { }
}

/// <summary>
/// Represents a model-visible compact XML-like element with compact attributes and valueless Boolean flags.
/// </summary>
/// <remarks>
/// Compact elements deliberately use familiar HTML-style flags such as <c>focused</c> instead of XML
/// attributes such as <c>focused="true"</c>. The result is model-facing markup rather than valid XML.
/// Nonempty attribute values without whitespace, control characters, or markup delimiters omit quotes;
/// all other attribute values remain quoted.
/// Elements pass priority by default and remain structurally self-closing when no child survives pruning.
/// </remarks>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class PromptCompactElement : PromptContainer
{
    /// <summary>
    /// Gets the validated element name.
    /// </summary>
    [Key(3)]
    public string Name { get; }

    /// <summary>
    /// Gets the ordered attributes emitted on this element.
    /// </summary>
    [Key(4)]
    public PromptAttributeCollection Attributes { get; }

    /// <summary>
    /// Gets the ordered valueless flags emitted on this element.
    /// </summary>
    [Key(5)]
    public PromptFlagCollection Flags { get; }

    /// <summary>
    /// Creates a compact XML-like prompt element.
    /// </summary>
    public PromptCompactElement(string name, params ReadOnlySpan<PromptNode?> children)
    {
        PromptXmlName.Validate(name, nameof(name));

        Name = name;
        PassPriority = true;
        foreach (var child in children)
        {
            if (child is not null) Children.Add(child);
        }

        Attributes = [];
        Flags = [];
    }

    [SerializationConstructor]
    private PromptCompactElement(List<PromptNode> children, string name, PromptAttributeCollection attributes, PromptFlagCollection flags) : base(
        children)
    {
        Name = name;
        Attributes = attributes;
        Flags = flags;
    }

    /// <summary>
    /// Adds or replaces an attribute after converting its value with invariant culture.
    /// </summary>
    public PromptCompactElement Attribute(string name, object? value)
    {
        Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds or replaces an attribute only when its value is not null.
    /// </summary>
    public PromptCompactElement AttributeNotNull(string name, object? value)
    {
        if (value is not null) Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds or replaces an attribute only when its value is neither null nor an empty string.
    /// </summary>
    public PromptCompactElement AttributeNotNullOrEmpty(string name, object? value)
    {
        if (value is not null && value is not string { Length: 0 }) Attributes[name] = value;
        return this;
    }

    /// <summary>
    /// Adds or removes a valueless Boolean flag.
    /// </summary>
    /// <param name="name">The compact markup flag name.</param>
    /// <param name="isPresent">Whether the flag should be present.</param>
    public PromptCompactElement Flag(string name, bool isPresent = true)
    {
        if (isPresent) Flags.Add(name);
        else Flags.Remove(name);
        return this;
    }
}
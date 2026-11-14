using System.Text.Json.Serialization;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Represents a span of content in an assistant chat message.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[MessagePackUnion(0, typeof(AssistantChatMessageTextSpan))]
[MessagePackUnion(1, typeof(AssistantChatMessageFunctionCallSpan))]
[MessagePackUnion(2, typeof(AssistantChatMessageReasoningSpan))]
[MessagePackUnion(3, typeof(AssistantChatMessageImageSpan))]
[MessagePackUnion(4, typeof(AssistantChatMessageVisualContextResetSpan))]
public abstract partial class AssistantChatMessageSpan
{
    /// <summary>
    /// Gets the direct change destination shared with its owning assistant and nested tools.
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public ChatMessageNode? Owner
    {
        get;
        internal set
        {
            if (ReferenceEquals(field, value))
            {
                return;
            }

            field = value;
            OnOwnerChanged(value);
        }
    }

    /// <summary>
    /// Transfers descendant ownership without subscribing to descendant property events.
    /// </summary>
    protected virtual void OnOwnerChanged(ChatMessageNode? owner) { }

    /// <summary>
    /// Invalidates the owning turn and records this node's durable mutation.
    /// </summary>
    protected void NotifyChanged()
    {
        Owner?.NotifyMessageChanged(isDurable: true);
    }

    protected void SetProperty<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyChanged();
    }

    /// <summary>
    /// Gets whether this span contains model output, independently of tool execution completion.
    /// Application-generated notices do not count as model output.
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public abstract bool HasOutputContent { get; }

    [Key(0)]
    public DateTimeOffset CreatedAt
    {
        get;
        set => SetProperty(ref field, value);
    } = DateTimeOffset.UtcNow;

    [Key(1)]
    public DateTimeOffset? FinishedAt
    {
        get;
        set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => FinishedAt.HasValue ? Math.Max((FinishedAt.Value - CreatedAt).TotalSeconds, 0) : 0;

    [Key(2)]
    public MetadataDictionary Metadata
    {
        get;
        set => SetProperty(ref field, value);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageTextSpan : AssistantChatMessageSpan, IDisposable
{
    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public override bool HasOutputContent => _contentMarkdownBuilder.Length > 0;

    [IgnoreMember]
    [JsonIgnore]
    public ChatTextBuffer ContentMarkdownBuilder => _contentMarkdownBuilder;

    [Key(3)]
    public string? Content
    {
        get => _contentMarkdownBuilder.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            _contentMarkdownBuilder.Append(value);
        }
    }

    [IgnoreMember] private readonly ChatTextBuffer _contentMarkdownBuilder = new();

    private void HandleContentMarkdownBuilderChanged(object? sender, EventArgs e)
    {
        NotifyChanged();
    }

    public void Dispose()
    {
        _contentMarkdownBuilder.Changed -= HandleContentMarkdownBuilderChanged;
    }

    [SerializationConstructor]
    public AssistantChatMessageTextSpan()
    {
        // A published span is read by the UI while its writer streams into this same buffer.
        _contentMarkdownBuilder.Changed += HandleContentMarkdownBuilderChanged;
    }

    public AssistantChatMessageTextSpan(string initialContent) : this()
    {
        _contentMarkdownBuilder.Append(initialContent);
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageFunctionCallSpan : AssistantChatMessageSpan, IHaveChatAttachments, IDisposable
{
    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public override bool HasOutputContent => Items.Any(message => !message.Calls.IsEmpty);

    /// <summary>
    /// Gets the function-call reference snapshot, independent of a dispatcher or binding collection.
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public IReadOnlyList<FunctionCallChatMessage> FunctionCalls => Volatile.Read(ref _functionCalls);

    [IgnoreMember]
    public IReadOnlyList<FunctionCallChatMessage> Items => FunctionCalls;

    [IgnoreMember]
    public int Count => FunctionCalls.Count;

    [IgnoreMember]
    [JsonIgnore]
    public IEnumerable<ChatAttachment> Attachments => FunctionCalls.SelectMany(message => message.Attachments);

    [Key(3)]
    private IEnumerable<FunctionCallChatMessage> SerializableFunctionCalls
    {
        get => FunctionCalls;
        set => _functionCalls = value.ToArray();
    }

    [IgnoreMember] private IReadOnlyList<FunctionCallChatMessage> _functionCalls = [];
    [IgnoreMember] private bool _isDisposed;

    [SerializationConstructor]
    public AssistantChatMessageFunctionCallSpan() { }

    public AssistantChatMessageFunctionCallSpan(FunctionCallChatMessage initialFunctionCall)
    {
        _functionCalls = [initialFunctionCall];
    }

    public AssistantChatMessageFunctionCallSpan(IEnumerable<FunctionCallChatMessage> initialFunctionCalls)
    {
        _functionCalls = [.. initialFunctionCalls];
    }

    public void Add(FunctionCallChatMessage functionCall) => AddRange([functionCall]);

    public void AddRange(IEnumerable<FunctionCallChatMessage> functionCalls)
    {
        var added = functionCalls.ToArray();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        foreach (var message in added)
        {
            message.Owner = Owner;
        }

        Volatile.Write(ref _functionCalls, _functionCalls.Concat(added).ToArray());

        NotifyChanged();
    }

    /// <inheritdoc />
    protected override void OnOwnerChanged(ChatMessageNode? owner)
    {
        foreach (var message in _functionCalls)
        {
            message.Owner = owner;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        foreach (var message in _functionCalls)
        {
            message.Owner = null;
            message.Dispose();
        }
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageReasoningSpan : AssistantChatMessageSpan, IDisposable
{
    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public override bool HasOutputContent => _reasoningMarkdownBuilder.Length > 0;

    [IgnoreMember]
    public ChatTextBuffer ReasoningMarkdownBuilder => _reasoningMarkdownBuilder;

    /// <summary>
    /// The reasoning output in Markdown format for serialization.
    /// </summary>
    [Key(3)]
    public string? ReasoningOutput
    {
        get => _reasoningMarkdownBuilder.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            _reasoningMarkdownBuilder.Append(value);
        }
    }

    [IgnoreMember] private readonly ChatTextBuffer _reasoningMarkdownBuilder = new();

    [SerializationConstructor]
    public AssistantChatMessageReasoningSpan()
    {
        _reasoningMarkdownBuilder.Changed += HandleReasoningMarkdownBuilderChanged;
    }

    public AssistantChatMessageReasoningSpan(string initialReasoning) : this()
    {
        _reasoningMarkdownBuilder.Append(initialReasoning);
    }

    private void HandleReasoningMarkdownBuilderChanged(object? sender, EventArgs e)
    {
        NotifyChanged();
    }

    public void Dispose()
    {
        _reasoningMarkdownBuilder.Changed -= HandleReasoningMarkdownBuilderChanged;
    }
}

[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class AssistantChatMessageImageSpan : AssistantChatMessageSpan, IHaveChatAttachments
{
    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public override bool HasOutputContent => ImageOutput is not null;

    // TODO: if we want to support multiple images in a single span, we can change this to a list of FileAttachment and update the Attachments property accordingly.
    // TODO: video output and other media types can be added in the future as well, just rename `AssistantChatMessageImageSpan` to `AssistantChatMessageMediaSpan` and add a list of media attachments.
    [Key(3)]
    public FileAttachment? ImageOutput
    {
        get;
        set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    [JsonIgnore]
    public IEnumerable<ChatAttachment> Attachments => ImageOutput is not null ? new[] { ImageOutput } : Array.Empty<ChatAttachment>();
}

/// <summary>
/// Persists a model-visible visual Context reset at its exact boundary within an Assistant invocation.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class AssistantChatMessageVisualContextResetSpan : AssistantChatMessageSpan
{
    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public override bool HasOutputContent => false;

    /// <summary>Gets the reset notice emitted at this boundary.</summary>
    [Key(3)]
    public VisualContextResetChatMessage Message { get; }

    /// <summary>Creates a visual Context reset span.</summary>
    [SerializationConstructor]
    public AssistantChatMessageVisualContextResetSpan(VisualContextResetChatMessage message)
    {
        Message = message;
        FinishedAt = CreatedAt;
    }
}
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Represents a span of content in an assistant chat message.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(AssistantChatMessageTextSpan))]
[Union(1, typeof(AssistantChatMessageFunctionCallSpan))]
[Union(2, typeof(AssistantChatMessageReasoningSpan))]
[Union(3, typeof(AssistantChatMessageImageSpan))]
[Union(4, typeof(AssistantChatMessageVisualContextResetSpan))]
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
    public override bool HasOutputContent => _contentMarkdownBuilder is { Length: > 0 };

    [IgnoreMember]
    [JsonIgnore]
    public ChatTextBuffer ContentMarkdownBuilder => EnsureContentMarkdownBuilder();

    [Key(3)]
    public string? Content
    {
        get => _contentMarkdownBuilder?.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            EnsureContentMarkdownBuilder().Append(value);
        }
    }

    [IgnoreMember] private ChatTextBuffer? _contentMarkdownBuilder;

    [MemberNotNull(nameof(_contentMarkdownBuilder))]
    private ChatTextBuffer EnsureContentMarkdownBuilder()
    {
        if (_contentMarkdownBuilder != null) return _contentMarkdownBuilder;
        _contentMarkdownBuilder = new ChatTextBuffer();
        _contentMarkdownBuilder.Changed += HandleContentMarkdownBuilderChanged;
        return _contentMarkdownBuilder;
    }

    private void HandleContentMarkdownBuilderChanged(object? sender, EventArgs e)
    {
        NotifyChanged();
    }

    public void Dispose()
    {
        _contentMarkdownBuilder?.Changed -= HandleContentMarkdownBuilderChanged;
    }

    [SerializationConstructor]
    public AssistantChatMessageTextSpan() { }

    public AssistantChatMessageTextSpan(string initialContent)
    {
        EnsureContentMarkdownBuilder().Append(initialContent);
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
    public override bool HasOutputContent => _reasoningMarkdownBuilder is { Length: > 0 };

    [IgnoreMember]
    public ChatTextBuffer ReasoningMarkdownBuilder => EnsureReasoningMarkdownBuilder();

    /// <summary>
    /// The reasoning output in Markdown format for serialization.
    /// </summary>
    [Key(3)]
    public string? ReasoningOutput
    {
        get => _reasoningMarkdownBuilder?.ToString();
        init
        {
            if (value is not { Length: > 0 }) return;

            EnsureReasoningMarkdownBuilder().Append(value);
        }
    }

    [IgnoreMember] private ChatTextBuffer? _reasoningMarkdownBuilder;

    [SerializationConstructor]
    public AssistantChatMessageReasoningSpan() { }

    public AssistantChatMessageReasoningSpan(string initialReasoning)
    {
        EnsureReasoningMarkdownBuilder().Append(initialReasoning);
    }

    [MemberNotNull(nameof(_reasoningMarkdownBuilder))]
    private ChatTextBuffer EnsureReasoningMarkdownBuilder()
    {
        if (_reasoningMarkdownBuilder != null) return _reasoningMarkdownBuilder;
        _reasoningMarkdownBuilder = new ChatTextBuffer();
        _reasoningMarkdownBuilder.Changed += HandleReasoningMarkdownBuilderChanged;
        return _reasoningMarkdownBuilder;
    }

    private void HandleReasoningMarkdownBuilderChanged(object? sender, EventArgs e)
    {
        NotifyChanged();
    }

    public void Dispose()
    {
        _reasoningMarkdownBuilder?.Changed -= HandleReasoningMarkdownBuilderChanged;
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
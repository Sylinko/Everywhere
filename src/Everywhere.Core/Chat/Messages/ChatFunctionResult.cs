using System.Text.Json.Serialization;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Represents structured function-result data that owns its persisted representation and related attachments.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
[Union(0, typeof(VisualTextFunctionResult))]
[Union(1, typeof(VisualAttachmentFunctionResult))]
public abstract partial class ChatFunctionResult : IHaveChatAttachments
{
    /// <summary>Gets the value presented to the model as the function result.</summary>
    [IgnoreMember]
    [JsonIgnore]
    public abstract object? Value { get; }

    /// <inheritdoc />
    [IgnoreMember]
    [JsonIgnore]
    public virtual IEnumerable<ChatAttachment> Attachments => [];
}

/// <summary>
/// Associates a function-result value with the visual Context that owns any Agent-visible IDs in that value.
/// </summary>
public abstract class VisualFunctionResult<T> : ChatFunctionResult, IHaveVisualReferences
{
    /// <inheritdoc />
    [Key(0)]
    public Guid? VisualContextId { get; }

    /// <summary>Gets the strongly typed function-result value.</summary>
    [Key(1)]
    public T Content { get; }

    /// <inheritdoc />
    public override object? Value => Content;

    /// <inheritdoc />
    public override IEnumerable<ChatAttachment> Attachments => Content is ChatAttachment attachment ? [attachment] : [];

    /// <summary>Creates a visual function result.</summary>
    [SerializationConstructor]
    protected VisualFunctionResult(Guid? visualContextId, T content)
    {
        if (visualContextId == Guid.Empty) throw new ArgumentException("A visual Context identity cannot be empty.", nameof(visualContextId));
        VisualContextId = visualContextId;
        Content = content;
    }
}

/// <summary>Represents a text result associated with a visual Context.</summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class VisualTextFunctionResult : VisualFunctionResult<string>
{
    /// <summary>Creates a visual text result.</summary>
    [SerializationConstructor]
    public VisualTextFunctionResult(Guid? visualContextId, string content) : base(visualContextId, content) { }
}

/// <summary>Represents an attachment result associated with a visual Context.</summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class VisualAttachmentFunctionResult : VisualFunctionResult<FileAttachment>
{
    /// <summary>Creates a visual attachment result.</summary>
    [SerializationConstructor]
    public VisualAttachmentFunctionResult(Guid? visualContextId, FileAttachment content) : base(visualContextId, content) { }
}
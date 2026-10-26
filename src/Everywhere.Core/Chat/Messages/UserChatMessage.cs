using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class UserChatMessage : ChatMessage, IHaveChatAttachments, IHaveVisualReferences
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.User;

    public override AuthorRole Role => AuthorRole.User;

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// Including attachments converted prompts that are invisible to the user.
    /// </summary>
    [Key(0)]
    public string Content
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(1)]
    public IReadOnlyList<ChatAttachment> Attachments
    {
        get;
        set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    IEnumerable<ChatAttachment> IHaveChatAttachments.Attachments => Attachments;

    /// <inheritdoc />
    [Key(2)]
    public Guid? VisualContextId
    {
        get;
        internal set => SetProperty(ref field, value);
    }

    [Key(3)]
    public override DateTimeOffset CreatedAt { get; }

    public override string ToString() => Content;

    /// <inheritdoc/>
    public UserChatMessage(string content, IReadOnlyList<ChatAttachment> attachments)
    {
        Content = content;
        Attachments = attachments;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    [SerializationConstructor]
    protected UserChatMessage(string content, IReadOnlyList<ChatAttachment> attachments, DateTimeOffset createdAt)
    {
        Content = content;
        Attachments = attachments;
        CreatedAt = createdAt;
    }

    /// <inheritdoc />
    public override ChatUserState Capture() => new(Content, Attachments);
}
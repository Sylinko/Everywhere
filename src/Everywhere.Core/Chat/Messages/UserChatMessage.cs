using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class UserChatMessage : ChatMessage, IHaveChatAttachments
{
    public override AuthorRole Role => AuthorRole.User;

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// Including attachments converted prompts that are invisible to the user.
    /// </summary>
    [Key(0)]
    [ObservableProperty]
    public partial string Content { get; set; }

    [Key(1)]
    public IReadOnlyList<ChatAttachment> Attachments { get; set; }

    [IgnoreMember]
    IEnumerable<ChatAttachment> IHaveChatAttachments.Attachments => Attachments;

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
}
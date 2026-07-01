using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public partial class UserChatMessage(string content, IReadOnlyList<ChatAttachment> attachments) : ChatMessage, IHaveChatAttachments
{
    public override AuthorRole Role => AuthorRole.User;

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// Including attachments converted prompts that are invisible to the user.
    /// </summary>
    [Key(0)]
    [ObservableProperty]
    public partial string Content { get; set; } = content;

    [Key(1)]
    public IReadOnlyList<ChatAttachment> Attachments { get; set; } = attachments;

    [IgnoreMember]
    IEnumerable<ChatAttachment> IHaveChatAttachments.Attachments => Attachments;

    [Key(3)]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString() => Content;
}
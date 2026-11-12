using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
[MessagePackUnion(0, typeof(RootChatMessage))]
[MessagePackUnion(1, typeof(AssistantChatMessage))]
[MessagePackUnion(2, typeof(UserChatMessage))]
[MessagePackUnion(3, typeof(ActionChatMessage))]
[MessagePackUnion(4, typeof(FunctionCallChatMessage))]
[MessagePackUnion(5, typeof(UserStrategyChatMessage))]
[MessagePackUnion(6, typeof(UserActionChatMessage))]
[MessagePackUnion(7, typeof(ContextCompressionChatMessage))]
[MessagePackUnion(8, typeof(VisualContextResetChatMessage))]
public abstract partial class ChatMessage : ObservableObject
{
    public abstract AuthorRole Role { get; }

    [IgnoreMember]
    [JsonIgnore]
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Gets a value indicating whether this message is excluded from the normal chat presentation.
    /// </summary>
    /// <remarks>
    /// Overrides that change this value after the node enters a chat context must raise a property
    /// change notification; otherwise the DynamicData visibility projections are not re-evaluated.
    /// </remarks>
    [IgnoreMember]
    [JsonIgnore]
    public virtual bool IsHidden => false;

    /// <summary>
    /// Gets the timestamp when the message was created.
    /// </summary>
    [IgnoreMember]
    public abstract DateTimeOffset CreatedAt { get; }
}

public interface IHaveChatAttachments
{
    IEnumerable<ChatAttachment> Attachments { get; }
}

/// <summary>
/// Identifies persisted chat content that contains Agent-visible references scoped to one visual Context.
/// </summary>
public interface IHaveVisualReferences
{
    /// <summary>Gets the identity of the visual Context that owns the referenced Agent IDs.</summary>
    Guid? VisualContextId { get; }
}
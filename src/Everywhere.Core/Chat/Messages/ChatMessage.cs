using System.Text.Json.Serialization;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
// Keep these tags aligned with ChatMessageKind values by convention.
// There is no generated or compiler-enforced link; update both declarations when adding a type.
[Union(0, typeof(RootChatMessage))]
[Union(1, typeof(AssistantChatMessage))]
[Union(2, typeof(UserChatMessage))]
[Union(3, typeof(ActionChatMessage))]
[Union(4, typeof(FunctionCallChatMessage))]
[Union(5, typeof(UserStrategyChatMessage))]
[Union(6, typeof(UserActionChatMessage))]
[Union(7, typeof(ContextCompressionChatMessage))]
[Union(8, typeof(VisualContextResetChatMessage))]
public abstract partial class ChatMessage
{
    /// <summary>
    /// Gets the durable message kind used by lightweight node projections.
    /// </summary>
    [IgnoreMember]
    [JsonIgnore]
    public abstract ChatMessageKind Kind { get; }

    [IgnoreMember]
    [JsonIgnore]
    public abstract AuthorRole Role { get; }

    /// <summary>
    /// Gets the node that owns this message's mutable state. Never serialized or UI-owned.
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
    /// Captures this message's current presentation values without subscribing or capturing mutable
    /// descendants. Message types without presentation values return null. Concurrent changes are
    /// reflected by subsequent invalidations rather than a transaction spanning all captured fields.
    /// </summary>
    public abstract ChatMessageState? Capture();

    /// <summary>
    /// Assigns the same direct change destination to mutable descendants when ownership changes.
    /// </summary>
    protected virtual void OnOwnerChanged(ChatMessageNode? owner) { }

    /// <summary>
    /// Invalidates the owning turn. Runtime-only changes opt out of persistence at their source.
    /// </summary>
    protected void NotifyChanged(bool isDurable = true)
    {
        Owner?.NotifyMessageChanged(isDurable);
    }

    /// <summary>
    /// Similar to ObservableObject.SetProperty, but using our own change notification mechanism. Avoids unnecessary allocations and is compatible with MessagePack serialization.
    /// </summary>
    /// <param name="field"></param>
    /// <param name="value"></param>
    /// <param name="isDurable"></param>
    /// <typeparam name="T"></typeparam>
    protected void SetProperty<T>(ref T field, T value, bool isDurable = true)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        NotifyChanged(isDurable);
    }

    [IgnoreMember]
    [JsonIgnore]
    public bool IsBusy
    {
        get;
        set => SetProperty(ref field, value, isDurable: false);
    }

    /// <summary>
    /// Gets a value indicating whether this message is excluded from the normal chat presentation.
    /// </summary>
    /// <remarks>
    /// Overrides that change this value after the node enters a chat data must notify the owner
    /// so the next turn snapshot re-evaluates visibility.
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
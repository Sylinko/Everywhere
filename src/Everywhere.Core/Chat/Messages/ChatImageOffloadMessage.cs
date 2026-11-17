using MessagePack;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Everywhere.Chat;

/// <summary>
/// Identifies a committed image occurrence rather than the file shared by multiple views.
/// Span and image indices remain stable as later tool cycles append output to the message.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public readonly partial record struct ChatImageOccurrence(
    [property: Key(0)] Guid NodeId,
    [property: Key(1)] int SpanIndex,
    [property: Key(2)] int ImageIndex);

/// <summary>
/// Records request-only omissions on one branch without changing its shared ancestor messages.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class ChatImageOffloadMessage(
    IReadOnlyList<ChatImageOccurrence> images,
    DateTimeOffset createdAt
) : ChatMessage
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.ImageOffload;

    /// <inheritdoc />
    public override ChatRole Role => new("action");

    /// <inheritdoc />
    public override bool IsHidden => true;

    /// <summary>
    /// Gets the images to replace with references on descendant requests.
    /// </summary>
    [Key(0)]
    public IReadOnlyList<ChatImageOccurrence> Images { get; } = images;

    /// <inheritdoc />
    [Key(1)]
    public override DateTimeOffset CreatedAt { get; } = createdAt;

    /// <inheritdoc />
    public override ChatMessageState? Capture() => null;
}
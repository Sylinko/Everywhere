using MessagePack;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class RootChatMessage : ChatMessage
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.Root;

    public override ChatRole Role => ChatRole.System;

    public override bool IsHidden => true;

    /// <summary>
    /// Note: This property is not serialized and will always return the default value.
    /// </summary>
    public override DateTimeOffset CreatedAt => default;

    /// <inheritdoc />
    public override ChatMessageState? Capture() => null;
}
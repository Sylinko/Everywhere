using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class RootChatMessage : ChatMessage
{
    public override AuthorRole Role => AuthorRole.System;

    public override bool IsHidden => true;

    /// <summary>
    /// Note: This property is not serialized and will always return the default value.
    /// </summary>
    public override DateTimeOffset CreatedAt => default;
}
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true)]
public sealed partial class RootChatMessage : ChatMessage
{
    public override AuthorRole Role => AuthorRole.System;
}
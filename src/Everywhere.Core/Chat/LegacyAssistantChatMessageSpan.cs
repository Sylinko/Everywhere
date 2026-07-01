using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// This class is obsolete and only used for deserializing old chat messages.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class LegacyAssistantChatMessageSpan
{
    [Key(0)]
    public string? Content { get; private init; }

    [Key(1)]
    public IReadOnlyList<FunctionCallChatMessage>? FunctionCalls { get; private init; }

    [Key(2)]
    public DateTimeOffset CreatedAt { get; private init; }

    [Key(3)]
    public DateTimeOffset FinishedAt { get; private init; }

    [Key(4)]
    public string? ReasoningOutput { get; private init; }

    [Key(5)]
    public DateTimeOffset? ReasoningFinishedAt { get; private init; }

    private LegacyAssistantChatMessageSpan() { }
}
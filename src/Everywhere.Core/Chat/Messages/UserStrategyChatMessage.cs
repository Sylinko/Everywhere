using Everywhere.StrategyEngine;
using MessagePack;

namespace Everywhere.Chat;

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class UserStrategyChatMessage : UserChatMessage
{
    [Key(4)]
    public Strategy Strategy { get; }

    [Key(5)]
    public PreprocessorResult? PreprocessorResult { get; }

    public UserStrategyChatMessage(
        string content,
        IReadOnlyList<ChatAttachment> attachments,
        Strategy strategy,
        PreprocessorResult? preprocessorResult = null) : base(content, attachments)
    {
        Strategy = strategy;
        PreprocessorResult = preprocessorResult;
    }

    [SerializationConstructor]
    private UserStrategyChatMessage(
        string content,
        IReadOnlyList<ChatAttachment> attachments,
        DateTimeOffset createdAt,
        Strategy strategy,
        PreprocessorResult? preprocessorResult) : base(content, attachments)
    {
        CreatedAt = createdAt;
        Strategy = strategy;
        PreprocessorResult = preprocessorResult;
    }
}
using Everywhere.AI;

namespace Everywhere.Common;

/// <summary>Terminal logical-request failure with the final classified cause and last ten attempts.</summary>
public sealed class ChatRequestException : HandledException
{
    /// <summary>Gets the final concrete cause without erasing its category through inheritance.</summary>
    public HandledChatException FinalFailure { get; }

    /// <summary>Gets the bounded diagnostic history.</summary>
    public IReadOnlyList<ChatRequestFailure> Failures { get; }

    /// <summary>Gets the total failures, including evicted records.</summary>
    public long TotalFailureCount { get; }

    /// <inheritdoc />
    public override IDynamicLocaleKey FriendlyMessageKey => FinalFailure.FriendlyMessageKey;

    /// <inheritdoc />
    public override bool IsExpected => FinalFailure.IsExpected;

    /// <summary>Retains the final normalized cause and logical-request history.</summary>
    public ChatRequestException(
        HandledChatException finalFailure,
        IReadOnlyList<ChatRequestFailure> failures,
        long totalFailureCount) : base(finalFailure)
    {
        FinalFailure = finalFailure;
        Failures = failures;
        TotalFailureCount = totalFailureCount;
    }
}
using System.Diagnostics;
using Everywhere.Common;
using Microsoft.SemanticKernel;

namespace Everywhere.AI;

/// <summary>Ordered request events. Content remains provisional until the attempt completes.</summary>
public abstract record ChatRequestUpdate
{
    /// <summary>Begins one SDK attempt. Its optional Activity is owned and ended by the executor.</summary>
    public sealed record AttemptStarted(int AttemptNumber, Activity? Activity) : ChatRequestUpdate;

    /// <summary>Forwards an existing SDK content delta.</summary>
    public sealed record Content(StreamingChatMessageContent Value) : ChatRequestUpdate;

    /// <summary>Closes a failed attempt. A null delay means the next move throws the terminal exception.</summary>
    public sealed record AttemptFailed(ChatRequestFailure Failure, TimeSpan? RetryDelay) : ChatRequestUpdate;
}

/// <summary>One retained attempt failure, without recursively capturing earlier attempts.</summary>
public sealed record ChatRequestFailure(int AttemptNumber, DateTimeOffset FailedAt, HandledChatException Exception);

/// <summary>Call-local overrides, used by connectivity probes to disable retries.</summary>
public sealed record ChatRequestOptions(int? MaxRetries = null);
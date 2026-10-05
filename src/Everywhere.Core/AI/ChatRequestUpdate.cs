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

    /// <summary>Closes the response stream; business validation still belongs to the consumer.</summary>
    public sealed record Completed(ChatResponseCompletion Completion) : ChatRequestUpdate;
}

/// <summary>One retained attempt failure, without recursively capturing earlier attempts.</summary>
public sealed record ChatRequestFailure(int AttemptNumber, DateTimeOffset FailedAt, HandledChatException Exception);

/// <summary>Call-local overrides, used by connectivity probes to disable retries.</summary>
public sealed record ChatRequestOptions(int? MaxRetries = null);

/// <summary>Preserves explicit completion evidence; absent or unfamiliar reasons remain compatible.</summary>
public sealed record ChatResponseCompletion(string? RawReason)
{
    /// <summary>Gets whether the provider explicitly reported unusable partial tool output.</summary>
    public bool HasIncompleteOutput => RawReason?.ToLowerInvariant() is
        "length" or "max_tokens" or "max_output_tokens" or "content_filter" or "safety" or "recitation";
}
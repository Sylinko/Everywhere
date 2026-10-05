namespace Everywhere.AI;

/// <summary>Recovery advice for one failure; this does not execute or schedule retries.</summary>
public abstract record ChatExceptionRecovery
{
    /// <summary>Stop; retrying identical input has no established benefit.</summary>
    public sealed record Stop : ChatExceptionRecovery;

    /// <summary>A transient failure allows retrying the same request.</summary>
    /// <param name="RetryAfter">The server-requested minimum delay, if supplied; the executor also applies local backoff.</param>
    public sealed record Retry(TimeSpan? RetryAfter = null) : ChatExceptionRecovery;

    /// <summary>The caller can recover by compressing context, not by replaying identical input.</summary>
    public sealed record RecoverContext : ChatExceptionRecovery;

    /// <summary>The operation was canceled by its caller.</summary>
    public sealed record Canceled : ChatExceptionRecovery;
}
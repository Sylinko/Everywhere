namespace Everywhere.Chat.Permissions;

public readonly record struct ConsentDecisionResult(ConsentDecision Decision, string? Reason)
{
    public static ConsentDecisionResult Deny(string? reason = null) => new(ConsentDecision.Deny, reason);

    public static ConsentDecisionResult AllowOnce => new(ConsentDecision.AllowOnce, null);

    public static ConsentDecisionResult AllowSession => new(ConsentDecision.AllowSession, null);

    public static ConsentDecisionResult AlwaysAllow => new(ConsentDecision.AlwaysAllow, null);

    public string FormatReason(string prefix)
    {
        return Reason.IsNullOrWhiteSpace() ? prefix : $"{prefix} Reason: {Reason}";
    }
}
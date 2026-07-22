using Everywhere.Chat.Plugins;

namespace Everywhere.Chat.Permissions;

/// <summary>
/// Represents the raw decision returned by the consent user interface.
/// </summary>
/// <remarks>
/// This value describes what the user selected, before the invocation context applies any
/// persisted or session-level approval state. It is therefore an input to the consent policy
/// layer, rather than the final result exposed to a chat plugin. See <see cref="RequestConsentResult"/>
/// for the effective result returned by <c>RequestConsentAsync</c>.
/// </remarks>
public readonly record struct ConsentDecision(ConsentDecisionKind Kind, string? Reason, RequestConsentCustomOption? CustomOption = null)
{
    public static ConsentDecision Deny(string? reason = null) => new(ConsentDecisionKind.Deny, reason);

    public static ConsentDecision AllowOnce => new(ConsentDecisionKind.AllowOnce, null);

    public static ConsentDecision AllowSession => new(ConsentDecisionKind.AllowSession, null);

    public static ConsentDecision AlwaysAllow => new(ConsentDecisionKind.AlwaysAllow, null);

    public string FormatReason(string prefix)
    {
        return Reason.IsNullOrWhiteSpace() ? prefix : $"{prefix} Reason: {Reason}";
    }
}

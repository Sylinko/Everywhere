namespace Everywhere.Chat.Permissions;

/// <summary>
/// Represents the user's consent decision for a permission request.
/// </summary>
public enum ConsentDecisionKind
{
    [DynamicLocaleKey(LocaleKey.ConsentDecision_Deny)]
    Deny = 0,
    [DynamicLocaleKey(LocaleKey.ConsentDecision_AllowOnce)]
    AllowOnce = 1,
    [DynamicLocaleKey(LocaleKey.ConsentDecision_AllowSession)]
    AllowSession = 2,
    [DynamicLocaleKey(LocaleKey.ConsentDecision_AlwaysAllow)]
    AlwaysAllow = 3,

    Custom = 4
}
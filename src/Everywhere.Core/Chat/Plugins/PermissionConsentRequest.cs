using Everywhere.Chat.Permissions;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Describes one outer tool approval request. Hooks customize its presentation while the host
/// retains responsibility for approval mode, user interaction, cancellation and remembered rules.
/// </summary>
public sealed class PermissionConsentRequest
{
    /// <summary>
    /// Gets or sets the localized heading shown for this invocation.
    /// </summary>
    public IDynamicLocaleKey HeaderKey { get; set; }

    /// <summary>
    /// Gets or sets the available choices for remembering function approval.
    /// </summary>
    public RequestConsentRememberMasks RememberMasks { get; set; } = RequestConsentRememberMasks.All;

    /// <summary>
    /// Gets or sets additional choices. Outer approval recognizes ToolConsentOption keys;
    /// values must remain valid without retaining a plugin implementation during user interaction.
    /// </summary>
    public IReadOnlyList<RequestConsentCustomOption>? CustomOptions { get; set; }

    /// <summary>
    /// Creates the standard heading from the function's display name and declared permissions.
    /// </summary>
    public PermissionConsentRequest(ChatFunction function)
    {
        HeaderKey = function.Permissions == ChatFunctionPermissions.None ?
            new FormattedDynamicLocaleKey(LocaleKey.ChatPluginConsentRequest_CommonNone_Header, function.HeaderKey) :
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatPluginConsentRequest_Common_Header,
                function.HeaderKey,
                new DirectLocaleKey(function.Permissions.I18N(LocaleKey.Common_Comma.I18N(), true)));
    }
}

/// <summary>
/// Identifies host-owned actions offered by outer tool approval without retaining plugin callbacks.
/// </summary>
public enum ToolConsentOption
{
    /// <summary>
    /// Allows this call and remembers approval for its tool group using the snapshot's stable Key.
    /// </summary>
    BypassPluginApproval
}
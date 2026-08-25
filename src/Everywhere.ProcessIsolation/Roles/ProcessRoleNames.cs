namespace Everywhere.ProcessIsolation.Roles;

/// <summary>
/// Converts process roles to their stable wire names and derives the default
/// per-user, per-desktop-session endpoint name.
/// </summary>
public static class ProcessRoleNames
{
    /// <summary>
    /// Returns the lower-case role name used in handshakes, diagnostics, and
    /// endpoint names. These values are protocol identifiers and must not be
    /// localized.
    /// </summary>
    public static string ToWireName(ProcessRole role) => role switch
    {
        ProcessRole.Main => "main",
        ProcessRole.Input => "input",
        ProcessRole.Automation => "automation",
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, null)
    };

    /// <summary>
    /// Builds the role endpoint identity. The session component scopes a pipe to
    /// the operating-system desktop/login session rather than an RPC nonce.
    /// Custom endpoint names are reserved for controlled diagnostics and do not
    /// change the peer-validation policy.
    /// </summary>
    public static string GetDefaultEndpoint(ProcessRole role, string desktopSessionId)
    {
        return GetEndpoint(ToWireName(role), desktopSessionId);
    }

    /// <summary>
    /// Builds the Main-control endpoint used by the short-lived Hosts controller.
    /// This is deliberately separate from <see cref="ProcessRole"/>: a controller
    /// is a command mode and never owns a Host role lease.
    /// </summary>
    public static string GetMainControlEndpoint(string desktopSessionId) =>
        GetEndpoint("main-control", desktopSessionId);

    private static string GetEndpoint(string wireName, string desktopSessionId)
    {
        var user = Environment.UserName;
        var safeUser = string.Concat(user.Select(static character => char.IsLetterOrDigit(character) ? character : '_'));
        var safeSession = string.Concat(desktopSessionId.Select(static character => char.IsLetterOrDigit(character) ? character : '_'));
        return $"Everywhere.ProcessIsolation.{safeUser}.{safeSession}.{wireName}";
    }
}
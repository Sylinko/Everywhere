using System.Security.Cryptography;
using System.Text;

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
        // macOS implements .NET named pipes with a Unix-domain socket below
        // the temporary directory. Keep the complete socket path below
        // sockaddr_un's 104-byte limit on every platform while retaining the
        // user/session scope in the stable identity hash.
        var identity = $"{Environment.UserName}\0{desktopSessionId}";
        var identityHash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
        var compactIdentity = Convert.ToHexString(identityHash.AsSpan(0, 8));
        return $"Everywhere.{compactIdentity}.{wireName}";
    }
}
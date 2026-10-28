using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Runtime identity exchanged during the RPC handshake. The identity is
/// the same-build/session policy input; endpoint ACLs and ownership provide the
/// local transport boundary, while the PID is retained for diagnostics and sanity checks.
/// </summary>
public sealed class RpcHandshakeIdentity
{
    /// <summary>Build identity used for exact same-version matching.</summary>
    public required string AssemblyInformationalVersion { get; init; }

    /// <summary>Closed wire identity owned by the process that created this identity.</summary>
    public required string WireName { get; init; }

    /// <summary>Operating-system process ID of the identity owner.</summary>
    public required long ProcessId { get; init; }

    /// <summary>Desktop/login session shared by the cooperating processes.</summary>
    public required string DesktopSessionId { get; init; }
}

/// <summary>Creates the small set of runtime facts exchanged during handshake.</summary>
public static class RpcRuntimeIdentity
{
    /// <summary>Builds an identity for the current process and the supplied role.</summary>
    public static RpcHandshakeIdentity CreateCurrent(ProcessRole role) =>
        CreateCurrent(ProcessRoleNames.ToWireName(role));

    /// <summary>Builds an identity for a closed wire identity outside <see cref="ProcessRole"/>.</summary>
    public static RpcHandshakeIdentity CreateCurrent(string wireName) => new()
    {
        AssemblyInformationalVersion = GetAssemblyInformationalVersion(),
        WireName = wireName,
        ProcessId = Environment.ProcessId,
        DesktopSessionId = GetDesktopSessionId()
    };

    /// <summary>
    /// Reads the informational version used by the process-isolation compatibility
    /// check from the entry assembly that owns the current process.
    /// </summary>
    public static string GetAssemblyInformationalVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(RpcRuntimeIdentity).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
            assembly.GetName().Version?.ToString() ??
            throw new InvalidOperationException("The current assembly has no informational version.");
    }

    /// <summary>
    /// Returns the desktop/login session shared by cooperating processes. Linux uses
    /// the explicit display or compositor identity rather than a POSIX process session,
    /// which can differ between terminal jobs, compositor launches and user services.
    /// Headless environments fall back to the platform's process session API.
    /// </summary>
    public static string GetDesktopSessionId()
    {
        if (OperatingSystem.IsLinux())
        {
            var sessionType = Environment.GetEnvironmentVariable("XDG_SESSION_TYPE");
            var display = Environment.GetEnvironmentVariable("DISPLAY");
            if (sessionType == "x11" && !string.IsNullOrEmpty(display))
            {
                return "x11:" + display;
            }

            var desktops = (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? string.Empty)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var compositorInstance = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
            if (!string.IsNullOrEmpty(compositorInstance) &&
                (desktops.Length == 0 || desktops.Contains("Hyprland", StringComparer.OrdinalIgnoreCase)))
            {
                return "hyprland:" + compositorInstance;
            }

            var waylandDisplay = Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");
            if (!string.IsNullOrEmpty(waylandDisplay))
            {
                var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
                var socketPath = waylandDisplay;
                if (Path.IsPathFullyQualified(waylandDisplay))
                {
                    socketPath = Path.GetFullPath(waylandDisplay);
                }
                else if (!string.IsNullOrEmpty(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory))
                {
                    socketPath = Path.GetFullPath(waylandDisplay, runtimeDirectory);
                }

                return "wayland:" + socketPath;
            }

            if (!string.IsNullOrEmpty(display))
            {
                return "x11:" + display;
            }
        }

        try
        {
            using var process = Process.GetCurrentProcess();
            return process.SessionId.ToString(CultureInfo.InvariantCulture);
        }
        catch (PlatformNotSupportedException)
        {
            // Some Unix runtimes do not expose Process.SessionId.
        }

        var environmentSession = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
        return string.IsNullOrWhiteSpace(environmentSession) ? "default" : environmentSession;
    }
}
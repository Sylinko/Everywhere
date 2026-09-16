using Everywhere.ProcessIsolation.Roles;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>Outcome of one platform-specific Hosts-control operation.</summary>
public enum HostsControlPlatformOutcome
{
    /// <summary>The requested platform operation completed.</summary>
    Succeeded,

    /// <summary>The platform has no service-mode integration and should launch Hosts directly.</summary>
    DirectLaunchRequired,

    /// <summary>The operation could not be completed.</summary>
    Failed,

    /// <summary>The operation conflicts with an existing integration owned by another application copy.</summary>
    Conflict,

    /// <summary>The user cancelled an interactive platform request.</summary>
    Cancelled
}

/// <summary>Result of one platform-specific Hosts-control operation.</summary>
/// <param name="Outcome">Operation outcome.</param>
/// <param name="DiagnosticDetail">Optional English diagnostic detail for logs and command-line output.</param>
public sealed record HostsControlPlatformResult(HostsControlPlatformOutcome Outcome, string? DiagnosticDetail = null)
{
    /// <summary>Creates a successful result.</summary>
    public static HostsControlPlatformResult Success(string? detail = null) => new(HostsControlPlatformOutcome.Succeeded, detail);

    /// <summary>Creates a result requesting direct Host launch.</summary>
    public static HostsControlPlatformResult Direct(string? detail = null) => new(HostsControlPlatformOutcome.DirectLaunchRequired, detail);

    /// <summary>Creates a failed result.</summary>
    public static HostsControlPlatformResult Failure(string detail) => new(HostsControlPlatformOutcome.Failed, detail);

    /// <summary>Creates an ownership-conflict result.</summary>
    public static HostsControlPlatformResult OwnershipConflict(string detail) => new(HostsControlPlatformOutcome.Conflict, detail);

    /// <summary>Creates a user-cancelled result.</summary>
    public static HostsControlPlatformResult Cancelled(string? detail = null) => new(HostsControlPlatformOutcome.Cancelled, detail);
}

/// <summary>
/// Supplies the small platform surface needed by the early <c>--hosts-control</c>
/// path. Platform entry points construct this object directly, before the full
/// application dependency graph exists.
/// </summary>
public interface IHostsControlPlatform
{
    /// <summary>Whether direct child processes inherit capabilities equivalent to service-mode launch.</summary>
    bool IsDirectLaunchEquivalentToServiceMode { get; }

    /// <summary>Starts the configured service-mode launcher in the requested interactive session.</summary>
    HostsControlPlatformResult StartServiceMode(int desktopSessionId);

    /// <summary>Installs or repairs service-mode integration for the current executable.</summary>
    HostsControlPlatformResult InstallServiceMode(HostsControlCommand command);

    /// <summary>Removes service-mode integration when it belongs to the current executable.</summary>
    HostsControlPlatformResult UninstallServiceMode();
}

/// <summary>Platform implementation for systems where Hosts always launch directly.</summary>
public sealed class DirectHostsControlPlatform : IHostsControlPlatform
{
    /// <summary>Shared stateless instance.</summary>
    public static DirectHostsControlPlatform Instance { get; } = new();

    private DirectHostsControlPlatform()
    {
    }

    /// <inheritdoc />
    public bool IsDirectLaunchEquivalentToServiceMode => true;

    /// <inheritdoc />
    public HostsControlPlatformResult StartServiceMode(int desktopSessionId) =>
        HostsControlPlatformResult.Direct("This platform starts Hosts directly and does not require service-mode integration.");

    /// <inheritdoc />
    public HostsControlPlatformResult InstallServiceMode(HostsControlCommand command) =>
        HostsControlPlatformResult.Success("This platform does not require service-mode installation.");

    /// <inheritdoc />
    public HostsControlPlatformResult UninstallServiceMode() =>
        HostsControlPlatformResult.Success("This platform does not require service-mode installation.");
}
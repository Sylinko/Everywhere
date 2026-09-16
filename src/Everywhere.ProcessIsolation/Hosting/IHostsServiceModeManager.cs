namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>Ownership and readability state of the platform service-mode integration.</summary>
public enum HostsServiceModeConfigurationState
{
    /// <summary>No integration is registered.</summary>
    NotConfigured,

    /// <summary>The integration launches the current executable.</summary>
    CurrentExecutable,

    /// <summary>The integration launches another Everywhere executable.</summary>
    OtherExecutable,

    /// <summary>An integration exists but its owner cannot be determined.</summary>
    Invalid,

    /// <summary>The platform configuration could not be queried.</summary>
    Unavailable
}

/// <summary>Current platform service-mode configuration.</summary>
/// <param name="State">Ownership and readability state.</param>
/// <param name="ConfiguredExecutablePath">Executable configured by the platform integration, when readable.</param>
/// <param name="LastTaskResult">Last platform launch result, when available.</param>
/// <param name="DiagnosticDetail">Optional diagnostic detail for logs and command-line output.</param>
public sealed record HostsServiceModeStatus(
    HostsServiceModeConfigurationState State,
    string? ConfiguredExecutablePath = null,
    int? LastTaskResult = null,
    string? DiagnosticDetail = null
);

/// <summary>Limited safety assessment performed before a portable copy requests service mode.</summary>
/// <param name="RequiresPortableAuthorization">Whether the elevated controller requires an explicit portable-copy authorization.</param>
/// <param name="RequiresWarning">Whether Main should warn before requesting the authorization.</param>
/// <param name="DiagnosticDetail">English assessment detail intended for logs.</param>
public sealed record HostsServiceModeEnvironmentAssessment(
    bool RequiresPortableAuthorization,
    bool RequiresWarning,
    string? DiagnosticDetail = null
);

/// <summary>Manages service-mode integration from the running Main process.</summary>
public interface IHostsServiceModeManager
{
    /// <summary>Queries the current configuration and its executable owner.</summary>
    HostsServiceModeStatus GetStatus();

    /// <summary>Assesses whether enabling service mode needs a portable-installation warning.</summary>
    HostsServiceModeEnvironmentAssessment AssessEnvironment();

    /// <summary>Requests elevated installation or repair through the early controller path.</summary>
    Task<HostsControlPlatformResult> RequestInstallAsync(
        bool shouldReplaceExisting,
        bool shouldAuthorizePortable,
        CancellationToken cancellationToken = default);

    /// <summary>Requests elevated removal through the early controller path.</summary>
    Task<HostsControlPlatformResult> RequestUninstallAsync(CancellationToken cancellationToken = default);
}
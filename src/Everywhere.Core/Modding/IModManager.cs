using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;

namespace Everywhere.Modding;

/// <summary>
/// Coordinates repository sources, dependency resolution, package installation, atomic state persistence,
/// license gating, rollback, and live <c>ModEngine</c> lifecycle management for the application host.
/// </summary>
public interface IModManager
{
    /// <summary>
    /// Occurs after installed mod state, runtime activation snapshots, or configured sources change.
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Gets the active host platform and API compatibility environment, or <see langword="null"/> if no host descriptor is available.
    /// </summary>
    ModHostEnvironment? HostEnvironment { get; }

    /// <summary>
    /// Gets whether hot retirement blocked further runtime activations until the application restarts.
    /// </summary>
    bool IsRestartRequired { get; }

    /// <summary>
    /// Gets host-owned diagnostics explaining why runtime retirement required a restart.
    /// </summary>
    IReadOnlyList<ModDiagnostic> RestartDiagnostics { get; }

    /// <summary>
    /// Gets the current host-owned snapshots of all installed mods.
    /// </summary>
    IReadOnlyList<ModSummary> InstalledMods { get; }

    /// <summary>
    /// Gets all active configured and built-in repository sources in priority order.
    /// </summary>
    IReadOnlyList<ModSourceInfo> Sources { get; }

    /// <summary>
    /// Fetches catalog indexes from enabled sources (skipping disabled sources and hidden unconfigured built-ins).
    /// </summary>
    Task<IReadOnlyList<ModSourceCatalogSnapshot>> FetchCatalogsAsync(
        bool shouldForceRefresh = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reads a UTF-8 README or license text document referenced by a repository source index.
    /// </summary>
    Task<string?> ReadDocumentAsync(string sourceId, string documentReference, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a UTF-8 README or license text document bundled inside an installed mod's active package directory.
    /// </summary>
    Task<string?> ReadInstalledDocumentAsync(string modId, string relativePath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Solves dependencies and checks required license confirmations for installing or updating <paramref name="modId"/> without mutating state.
    /// </summary>
    Task<ModOperationResult> PlanInstallAsync(
        string modId,
        string? preferredSourceId = null,
        string? versionRange = null,
        string? channel = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Installs <paramref name="modId"/> along with any missing compatible dependencies and activates it immediately when possible.
    /// </summary>
    Task<ModOperationResult> InstallAsync(
        string modId,
        string? preferredSourceId = null,
        string? versionRange = null,
        string? channel = null,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Updates an already-installed repository mod to the highest compatible version (or channel/range target).
    /// </summary>
    Task<ModOperationResult> UpdateAsync(
        string modId,
        string? versionRange = null,
        string? channel = null,
        IReadOnlyDictionary<string, ModAcceptedLicense>? acceptedLicenses = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Imports a standalone local <c>.zip</c> mod archive into the immutable package store and activates it.
    /// </summary>
    Task<ModOperationResult> ImportZipAsync(string zipPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports an unpacked local development directory, tracking its SHA-256 fingerprint for automatic reload on restart.
    /// </summary>
    Task<ModOperationResult> ImportDirectoryAsync(string directoryPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-imports and hot-reloads an unpacked development directory mod tracked under <paramref name="modId"/>.
    /// </summary>
    Task<ModOperationResult> ReloadLocalDirectoryAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables an installed mod, optionally cascading to reverse-dependent mods when disabling
    /// or to required disabled dependencies when enabling.
    /// </summary>
    Task<ModOperationResult> SetEnabledAsync(
        string modId,
        bool isEnabled,
        bool shouldCascadeDependents = false,
        bool shouldCascadeDependencies = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Uninstalls an installed mod immediately or queues cold-start removal when hot unloading requires a restart.
    /// </summary>
    Task<ModOperationResult> UninstallAsync(
        string modId,
        bool shouldDeleteData = false,
        bool shouldCascadeDependents = false,
        bool shouldRemoveOrphanDependencies = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Rolls back an installed mod to its retained <c>N=1</c> prior installation slot.
    /// </summary>
    Task<ModOperationResult> RollbackAsync(string modId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns installed consumers that depend on <paramref name="modId"/> in consumer-first order.
    /// </summary>
    IReadOnlyList<string> GetReverseDependents(string modId, bool shouldIncludeDisabled = false);

    /// <summary>
    /// Returns auto-installed dependencies that would become unreachable after removing <paramref name="removingModIds"/>.
    /// </summary>
    IReadOnlyList<string> GetOrphanAutoDependencies(IReadOnlySet<string> removingModIds);

    /// <summary>
    /// Adds and persists a custom HTTP(S) or local-directory repository source.
    /// </summary>
    Task<ModOperationResult> AddCustomSourceAsync(
        string urlOrPath,
        string? id = null,
        string[]? mirrors = null,
        bool isUntrustedConfirmed = false,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Removes a user-configured custom repository source. Built-in sources cannot be removed.
    /// </summary>
    Task<ModOperationResult> RemoveCustomSourceAsync(string sourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Enables or disables a repository source.
    /// </summary>
    Task<ModOperationResult> SetSourceEnabledAsync(string sourceId, bool isEnabled, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists source priority order across built-in and custom sources.
    /// </summary>
    Task<ModOperationResult> ReorderSourcesAsync(IReadOnlyList<string> orderedSourceIds, CancellationToken cancellationToken = default);
}

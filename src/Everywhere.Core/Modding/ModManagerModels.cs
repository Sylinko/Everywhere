using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Hosting.Resolution;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding;

/// <summary>
/// Configures persistent paths, host metadata overrides, official repository defaults, and runtime policy for <see cref="ModManager"/>.
/// </summary>
public sealed record ModManagerOptions
{
    /// <summary>
    /// Gets the optional path to <c>mods-state.json</c>. Defaults to <c>~/.everywhere/mods-state.json</c>.
    /// </summary>
    public string? StateFilePath { get; init; }

    /// <summary>
    /// Gets the optional root directory for the immutable extracted package store.
    /// Defaults to the rebuildable application cache under <c>cache/mods</c>.
    /// </summary>
    public string? PackageStoreDirectory { get; init; }

    /// <summary>
    /// Gets the optional root directory for per-mod persistent user data.
    /// Defaults to <c>~/.everywhere/mods-data</c>.
    /// </summary>
    public string? DataDirectory { get; init; }

    /// <summary>
    /// Gets the optional staging directory used while validating incoming packages.
    /// Defaults to <c>cache/mods/staging</c>.
    /// </summary>
    public string? StagingDirectory { get; init; }

    /// <summary>
    /// Gets the optional cache directory for HTTP repository indexes and streamed artifact verification.
    /// Defaults to <c>cache/mods/http-cache</c>.
    /// </summary>
    public string? HttpCacheDirectory { get; init; }

    /// <summary>
    /// Gets an optional override for the host runtime and version environment.
    /// When omitted, metadata is read from <c>mod-host.json</c> under <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public ModHostEnvironment? HostEnvironment { get; init; }

    /// <summary>
    /// Gets an optional override for the shared host assembly catalog passed to <see cref="ModEngine"/>.
    /// When omitted, assemblies are loaded from the deployment's <c>HostReferenceSet</c>.
    /// </summary>
    public HostAssemblyCatalog? HostAssemblyCatalog { get; init; }

    /// <summary>
    /// Gets an optional override for the official repository index URL.
    /// When null, <see cref="ModdingConstants.OfficialSourceUrl"/> is used; an empty value disables the official source.
    /// </summary>
    public string? OfficialSourceUrl { get; init; }

    /// <summary>
    /// Gets an optional override for comma- or whitespace-separated official mirror index URLs.
    /// When null, <see cref="ModdingConstants.OfficialSourceMirrors"/> is used.
    /// </summary>
    public string? OfficialSourceMirrors { get; init; }

    /// <summary>
    /// Gets an optional override for the pinned ECDSA P-256 SPKI public key of the official repository.
    /// When null, <see cref="ModdingConstants.OfficialSourcePublicKey"/> is used; an empty value disables the official source.
    /// </summary>
    public string? OfficialSourcePublicKey { get; init; }

    /// <summary>
    /// Gets the runtime activation and collection policy passed to <see cref="ModEngine"/>.
    /// </summary>
    public ModEngineOptions EngineOptions { get; init; } = new();
}

/// <summary>
/// Captures host-owned state and metadata for an installed mod without retaining collectible mod objects.
/// </summary>
public sealed record ModSummary
{
    /// <summary>
    /// Gets the stable lowercase mod identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the currently active installed version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the source identifier or local import marker (<c>local-zip</c> / <c>local-dir</c>).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets whether the user intends this mod to be active.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets whether the mod was explicitly installed or automatically pulled as a dependency.
    /// </summary>
    public ModInstallIntent Intent { get; init; }

    /// <summary>
    /// Gets the current runtime activation state.
    /// </summary>
    public ModState State { get; init; }

    /// <summary>
    /// Gets why the mod was skipped when <see cref="State"/> is <see cref="ModState.Skipped"/>.
    /// </summary>
    public ModSkipReason? SkipReason { get; init; }

    /// <summary>
    /// Gets localized display names keyed by BCP 47 language tag.
    /// </summary>
    public IReadOnlyDictionary<string, string> Name { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets localized summary descriptions keyed by BCP 47 language tag.
    /// </summary>
    public IReadOnlyDictionary<string, string> Description { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets localized README relative paths inside the active installation.
    /// </summary>
    public IReadOnlyDictionary<string, string> Readme { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the optional author string declared in <c>mod.json</c>.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the resolved filesystem path to the mod's icon inside its active installation, when available.
    /// </summary>
    public string? IconPath { get; init; }

    /// <summary>
    /// Gets the optional homepage URL declared in <c>mod.json</c>.
    /// </summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// Gets the optional source repository metadata declared in <c>mod.json</c>.
    /// </summary>
    public ModRepositoryInfo? Repository { get; init; }

    /// <summary>
    /// Gets the optional license metadata declared in <c>mod.json</c>.
    /// </summary>
    public ModLicense? License { get; init; }

    /// <summary>
    /// Gets the universal or platform-selected entry declaration from <c>mod.json</c>, when available.
    /// </summary>
    public ModEntry? Entry { get; init; }

    /// <summary>
    /// Gets whether a single prior installation slot is retained for rollback.
    /// </summary>
    public bool HasRollback { get; init; }

    /// <summary>
    /// Gets the retained prior version available for rollback, if any.
    /// </summary>
    public string? PreviousVersion { get; init; }

    /// <summary>
    /// Gets a deferred cold-start mutation queued when hot unloading required a restart.
    /// </summary>
    public ModPendingOperation? PendingOperation { get; init; }

    /// <summary>
    /// Gets whether this mod has a pending update, removal, or disablement waiting for application restart.
    /// </summary>
    public bool IsPendingRestart { get; init; }

    /// <summary>
    /// Gets the tracked unpacked development directory when imported from <see cref="ModStateRecord.LocalDirectorySourceId"/>.
    /// </summary>
    public string? LocalDirectoryPath { get; init; }

    /// <summary>
    /// Gets the preferred distribution channel saved for updates.
    /// </summary>
    public string? PreferredChannel { get; init; }

    /// <summary>
    /// Gets an optional pinned version configured for this mod.
    /// </summary>
    public string? PinnedVersion { get; init; }

    /// <summary>
    /// Gets host-owned runtime diagnostics associated with this mod.
    /// </summary>
    public IReadOnlyList<ModDiagnostic> Diagnostics { get; init; } = [];
}

/// <summary>
/// Describes a configured or built-in mod repository source.
/// </summary>
public sealed record ModSourceInfo
{
    /// <summary>
    /// Gets the unique source identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the index URL, directory path, or built-in URI.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets configured fallback mirror index URLs.
    /// </summary>
    public IReadOnlyList<string> Mirrors { get; init; } = [];

    /// <summary>
    /// Gets whether this source participates in catalog queries and updates.
    /// </summary>
    public bool IsEnabled { get; init; }

    /// <summary>
    /// Gets whether this source is built-in and cannot be removed.
    /// </summary>
    public bool IsBuiltIn { get; init; }

    /// <summary>
    /// Gets whether cryptographic signatures are enforced for indexes and artifacts from this source.
    /// </summary>
    public bool IsSignatureRequired { get; init; }

    /// <summary>
    /// Gets whether the user acknowledged an unsigned custom source warning.
    /// </summary>
    public bool IsUntrustedConfirmed { get; init; }

    /// <summary>
    /// Gets the pinned ECDSA P-256 public key when present.
    /// </summary>
    public string? PublicKey { get; init; }
}

/// <summary>
/// Captures the fetched catalog index (or load error) for a repository source.
/// </summary>
public sealed record ModSourceCatalogSnapshot(ModSourceInfo Source, ModIndex? Index, string? Error);

/// <summary>
/// Classifies the outcome of a high-level <see cref="IModManager"/> operation.
/// </summary>
public enum ModOperationStatus
{
    /// <summary>
    /// The operation completed cleanly without pending restart or unresolved confirmations.
    /// </summary>
    Applied,

    /// <summary>
    /// State was updated, though one or more mods reported diagnostics or initialization failures.
    /// </summary>
    CompletedWithErrors,

    /// <summary>
    /// Active owners could not retire cleanly during hot unload; the change is queued for next startup.
    /// </summary>
    RestartRequired,

    /// <summary>
    /// Explicit user confirmation (EULA/license acceptance or cascading dependents) is required before applying.
    /// </summary>
    ConfirmationRequired,

    /// <summary>
    /// Validation, dependency solving, source lookup, or package import failed before mutation.
    /// </summary>
    Failed
}

/// <summary>
/// Identifies a mod version whose license terms require explicit user acceptance prior to installation or update.
/// </summary>
public sealed record ModLicenseRequirement(
    string ModId,
    string Version,
    string SourceId,
    string Spdx,
    string? LicenseDocumentReference
);

/// <summary>
/// Reports the structured result of a mod installation, update, enablement, uninstall, or source operation.
/// </summary>
public sealed record ModOperationResult
{
    /// <summary>
    /// Gets the overall operation status.
    /// </summary>
    public required ModOperationStatus Status { get; init; }

    /// <summary>
    /// Gets host-owned runtime and validation diagnostics produced by the operation.
    /// </summary>
    public IReadOnlyList<ModDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// Gets dependent mod IDs that would be impacted when disabling or uninstalling a shared dependency.
    /// </summary>
    public IReadOnlyList<string> AffectedDependents { get; init; } = [];

    /// <summary>
    /// Gets installed-but-disabled dependency mod IDs that would also be enabled when enabling a consumer mod.
    /// </summary>
    public IReadOnlyList<string> AffectedDependencies { get; init; } = [];

    /// <summary>
    /// Gets pending license agreements that must be accepted before proceeding.
    /// </summary>
    public IReadOnlyList<ModLicenseRequirement> PendingLicenses { get; init; } = [];

    /// <summary>
    /// Gets the dependency solver output when planning, installing, updating, or failing on dependency constraints.
    /// </summary>
    public ModSolveResult? SolveResult { get; init; }

    /// <summary>
    /// Gets a human-readable summary message describing the outcome or failure.
    /// </summary>
    public string? Message { get; init; }
}

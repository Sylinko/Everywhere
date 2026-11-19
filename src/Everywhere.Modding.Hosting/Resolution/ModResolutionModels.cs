using System.Text.Json.Serialization;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Resolution;

/// <summary>
/// Distinguishes user-requested mods from dependencies automatically pulled to satisfy another mod.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ModInstallIntent>))]
public enum ModInstallIntent
{
    /// <summary>
    /// Explicitly installed or imported by the user.
    /// </summary>
    Explicit,

    /// <summary>
    /// Automatically pulled to satisfy a required dependency of an explicit mod.
    /// </summary>
    AutoDependency
}

/// <summary>
/// Captures host platform and API compatibility facts used during offline dependency resolution.
/// </summary>
public sealed record ModHostEnvironment(
    string RuntimeIdentifier,
    string HostVersion,
    string ModApiVersion,
    string HostFramework
);

/// <summary>
/// Supplies one validated repository index keyed by its host source identifier.
/// </summary>
public sealed record ModSourceCatalogEntry(string SourceId, ModIndex Index);

/// <summary>
/// Represents an already-installed mod known to the state store prior to planning changes.
/// </summary>
public sealed record InstalledModSnapshot(
    string Id,
    string Version,
    string SourceId,
    bool IsEnabled,
    ModInstallIntent Intent,
    UniversalModEntry? Entry
);

/// <summary>
/// Expresses a desired target mod requirement for installation, update, enablement, or pinned resolution.
/// </summary>
public sealed record ModTargetRequirement(
    string Id,
    string? VersionRange = null,
    string? PreferredChannel = null,
    string? PreferredSourceId = null,
    ModInstallIntent Intent = ModInstallIntent.Explicit
);

/// <summary>
/// Defines all inputs required to solve a conflict-free, host-compatible cross-source mod dependency graph.
/// </summary>
public sealed record ModSolveRequest
{
    /// <summary>
    /// Gets the host platform and version environment.
    /// </summary>
    public required ModHostEnvironment Host { get; init; }

    /// <summary>
    /// Gets available repository catalogs in priority order.
    /// </summary>
    public IReadOnlyList<ModSourceCatalogEntry> Sources { get; init; } = [];

    /// <summary>
    /// Gets currently installed mods in the profile.
    /// </summary>
    public IReadOnlyList<InstalledModSnapshot> Installed { get; init; } = [];

    /// <summary>
    /// Gets explicitly requested additions, upgrades, or constraint targets.
    /// </summary>
    public IReadOnlyList<ModTargetRequirement> Targets { get; init; } = [];

    /// <summary>
    /// Gets mod identifiers excluded from the desired active set (such as pending uninstall/disable targets).
    /// </summary>
    public IReadOnlySet<string> ExcludedModIds { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Gets whether currently installed compatible versions are preferred over upgrading unless constrained or targeted.
    /// </summary>
    public bool ShouldKeepInstalledVersions { get; init; } = true;
}

/// <summary>
/// Describes one selected package in dependency-first topological order produced by <see cref="ModDependencySolver"/>.
/// </summary>
public sealed record ResolvedModPackage
{
    /// <summary>
    /// Gets the resolved mod identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the resolved exact version string.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the source identifier providing this version (or the installed source for local packages).
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>
    /// Gets the effective ownership intent for persistence.
    /// </summary>
    public required ModInstallIntent Intent { get; init; }

    /// <summary>
    /// Gets the host-compatible platform entry selected for this version.
    /// </summary>
    public required UniversalModEntry SelectedEntry { get; init; }

    /// <summary>
    /// Gets the matching repository entry when backed by a catalog source.
    /// </summary>
    public ModIndexEntry? IndexEntry { get; init; }

    /// <summary>
    /// Gets the matching repository version metadata when backed by a catalog source.
    /// </summary>
    public ModIndexVersion? IndexVersion { get; init; }

    /// <summary>
    /// Gets whether the selected version matches the already-installed version from the same source.
    /// </summary>
    public bool IsAlreadyInstalledVersion { get; init; }
}

/// <summary>
/// Captures a human-readable causal constraint involved in an unsatisfiable dependency conflict.
/// </summary>
public sealed record ModConflictClause(
    string? ConsumerId,
    string? ConsumerVersion,
    string DependencyId,
    string? RequiredRange,
    string Reason
);

/// <summary>
/// Represents the result of solving a <see cref="ModSolveRequest"/>.
/// </summary>
public sealed record ModSolveResult
{
    /// <summary>
    /// Gets whether a compatible, acyclic, conflict-free dependency assignment was found.
    /// </summary>
    public required bool IsSuccess { get; init; }

    /// <summary>
    /// Gets the resolved packages in dependency-first topological order when <see cref="IsSuccess"/> is <see langword="true"/>.
    /// </summary>
    public IReadOnlyList<ResolvedModPackage> Packages { get; init; } = [];

    /// <summary>
    /// Gets causal conflict clauses when <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    public IReadOnlyList<ModConflictClause> Conflicts { get; init; } = [];

    /// <summary>
    /// Gets a concise diagnostic summary describing the root conflict when unsatisfiable.
    /// </summary>
    public string? FailureSummary { get; init; }
}

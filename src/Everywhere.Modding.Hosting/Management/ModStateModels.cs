using Everywhere.Modding.Hosting.Resolution;

namespace Everywhere.Modding.Hosting.Management;

/// <summary>
/// Persists a configured static repository source and its optional TOFU-pinned ECDSA P-256 public key.
/// </summary>
public sealed record ModSourceRecord
{
    /// <summary>
    /// Gets the host-assigned unique source identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the primary static <c>index.json</c> URL (HTTP, HTTPS, or file URL) or built-in source URI.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets or sets additional mirror <c>index.json</c> URLs configured by the host or user.
    /// </summary>
    public string[] Mirrors { get; set; } = [];

    /// <summary>
    /// Gets or sets whether this source is enabled for discovery and updates.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this source is built-in/immutable in the host UI.
    /// </summary>
    public bool IsBuiltIn { get; set; }

    /// <summary>
    /// Gets or sets whether the user acknowledged an unsigned or unverified custom source warning.
    /// </summary>
    public bool IsUntrustedConfirmed { get; set; }

    /// <summary>
    /// Gets or sets the pinned ECDSA P-256 public key when trusted.
    /// </summary>
    public string? PublicKey { get; set; }
}

/// <summary>
/// References an immutable installed package directory inside <c>ModPackageStore.RootDirectory</c>.
/// </summary>
public sealed record ModInstallationSlot
{
    /// <summary>
    /// Gets the unique installation identifier matching <c>ModInstallation.InstallationId</c>.
    /// </summary>
    public required string InstallationId { get; init; }

    /// <summary>
    /// Gets the installed exact semantic version.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the package-store relative directory path (using <c>/</c> separators).
    /// </summary>
    public required string RelativeDirectory { get; init; }
}

/// <summary>
/// Distinguishes deferred cold-start mutations queued when hot retirement returned <see cref="ModApplyStatus.RestartRequired"/>.
/// </summary>
public enum ModPendingOperationKind
{
    /// <summary>
    /// Switch the active slot to <see cref="ModPendingOperation.TargetInstallation"/> before loading mods at next startup.
    /// </summary>
    Update,

    /// <summary>
    /// Remove the mod and retire its slots before loading mods at next startup.
    /// </summary>
    Uninstall
}

/// <summary>
/// Represents a deferred update or uninstall queued until the next cold startup.
/// </summary>
public sealed record ModPendingOperation
{
    /// <summary>
    /// Gets the deferred operation kind.
    /// </summary>
    public required ModPendingOperationKind Kind { get; init; }

    /// <summary>
    /// Gets the staged replacement installation slot when <see cref="Kind"/> is <see cref="ModPendingOperationKind.Update"/>.
    /// </summary>
    public ModInstallationSlot? TargetInstallation { get; init; }

    /// <summary>
    /// Gets or sets whether persistent mod user data under <c>DataDirectory</c> should also be deleted on uninstall.
    /// </summary>
    public bool ShouldDeleteData { get; set; }
}

/// <summary>
/// Records explicit user acceptance of a mod's EULA or license text.
/// </summary>
public sealed record ModAcceptedLicense
{
    /// <summary>
    /// Gets the accepted SPDX identifier or custom <c>LicenseRef-</c>.
    /// </summary>
    public required string Spdx { get; init; }

    /// <summary>
    /// Gets the optional lowercase hexadecimal SHA-256 hash of the accepted license document text.
    /// </summary>
    public string? TextSha256 { get; init; }

    /// <summary>
    /// Gets the Unix timestamp in seconds when acceptance was recorded.
    /// </summary>
    public long AcceptedAtUnixSeconds { get; init; }
}

/// <summary>
/// Persists runtime state, source binding, local development sync tracking, and <c>N=1</c> rollback slot for one mod.
/// </summary>
public sealed record ModStateRecord
{
    /// <summary>
    /// Reserved source identifier for local standalone <c>.zip</c> imports.
    /// </summary>
    public const string LocalZipSourceId = "local-zip";

    /// <summary>
    /// Reserved source identifier for local unpacked development directory imports.
    /// </summary>
    public const string LocalDirectorySourceId = "local-dir";

    /// <summary>
    /// Gets the stable lowercase mod identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets the owning repository source identifier or local import marker.
    /// </summary>
    public required string SourceId { get; set; }

    /// <summary>
    /// Gets or sets whether the user desires this mod to be active.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether this mod was explicitly requested or automatically installed as a dependency.
    /// </summary>
    public ModInstallIntent Intent { get; set; } = ModInstallIntent.Explicit;

    /// <summary>
    /// Gets or sets the preferred distribution channel when resolving repository updates.
    /// </summary>
    public string? PreferredChannel { get; set; }

    /// <summary>
    /// Gets or sets an optional exact version pin requested by the user.
    /// </summary>
    public string? PinnedVersion { get; set; }

    /// <summary>
    /// Gets or sets the unpacked source directory tracked for automatic reload when <see cref="SourceId"/> is <see cref="LocalDirectorySourceId"/>.
    /// </summary>
    public string? LocalDirectoryPath { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 directory fingerprint corresponding to the current imported dev build.
    /// </summary>
    public string? LocalDirectoryFingerprint { get; set; }

    /// <summary>
    /// Gets or sets the currently active package slot.
    /// </summary>
    public ModInstallationSlot? ActiveInstallation { get; set; }

    /// <summary>
    /// Gets or sets the single retained prior package slot (<c>N=1</c>) available for immediate rollback.
    /// </summary>
    public ModInstallationSlot? PreviousInstallation { get; set; }

    /// <summary>
    /// Gets or sets a deferred cold-start operation queued while hot unloading required a restart.
    /// </summary>
    public ModPendingOperation? PendingOperation { get; set; }

    /// <summary>
    /// Gets or sets the recorded license acceptance state for mods requiring user consent.
    /// </summary>
    public ModAcceptedLicense? AcceptedLicense { get; set; }
}

/// <summary>
/// Root document persisted atomically to <c>mods-state.json</c>.
/// </summary>
public sealed record ModStateDocument
{
    /// <summary>
    /// Gets or sets the state schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets or sets configured repository sources in user display/priority order.
    /// </summary>
    public List<ModSourceRecord> Sources { get; set; } = [];

    /// <summary>
    /// Gets or sets installed or tracked mods keyed by stable mod identifier.
    /// </summary>
    public Dictionary<string, ModStateRecord> Mods { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets mod IDs whose repository <c>isDefaultEnabled</c> suggestion has already been applied once.
    /// </summary>
    public HashSet<string> SeenDefaultModIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets package-store relative directories pending cleanup after locks or restarts clear.
    /// </summary>
    public HashSet<string> RetiredDirectories { get; set; } = new(StringComparer.Ordinal);
}

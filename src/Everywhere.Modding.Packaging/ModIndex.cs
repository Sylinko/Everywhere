namespace Everywhere.Modding.Packaging;

/// <summary>
/// Represents a static v1 mod repository index published alongside downloadable packages or embedded assets.
/// </summary>
public sealed record ModIndex
{
    /// <summary>
    /// Gets or sets the static repository index format version. Currently only format version <c>1</c> is supported.
    /// </summary>
    public int Format { get; set; } = 1;

    /// <summary>
    /// Gets the stable lowercase identifier of this repository source.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets localized display titles keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Title
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized repository descriptions keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Description
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the Unix timestamp in seconds when this index snapshot was generated.
    /// </summary>
    public long GeneratedAt { get; init; }

    /// <summary>
    /// Gets the optional ECDSA P-256 public key used to verify this index and its artifact signatures.
    /// </summary>
    public string? PublicKey { get; init; }

    /// <summary>
    /// Gets the optional ECDSA P-256 signature over the canonical index payload.
    /// </summary>
    public string? Signature { get; init; }

    /// <summary>
    /// Gets or sets mirror <c>index.json</c> URLs tried alongside the primary source endpoint.
    /// </summary>
    public string[] Mirrors { get; set; } = [];

    /// <summary>
    /// Gets or sets repository-defined taxonomy declarations for categories, labels, and sort keys.
    /// </summary>
    public ModIndexMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets listed mods in publisher-preferred display order.
    /// </summary>
    public ModIndexEntry[] Mods { get; set; } = [];
}

/// <summary>
/// Declares opaque repository categories, labels, and sort keys rendered by the discovery UI.
/// </summary>
public sealed record ModIndexMetadata
{
    /// <summary>
    /// Gets or sets declared category IDs mapped to localized display metadata.
    /// </summary>
    public Dictionary<string, ModIndexDeclaration> Categories { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets declared label IDs mapped to localized display metadata.
    /// </summary>
    public Dictionary<string, ModIndexDeclaration> Labels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets or sets declared sort key IDs mapped to localized titles and default direction.
    /// </summary>
    public Dictionary<string, ModIndexSortKeyDeclaration> SortKeys { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Describes a repository-declared category or label display title.
/// </summary>
public sealed record ModIndexDeclaration
{
    /// <summary>
    /// Gets or sets localized display titles keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Title
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Describes a repository-declared numeric sort key and its default direction.
/// </summary>
public sealed record ModIndexSortKeyDeclaration
{
    /// <summary>
    /// Gets or sets localized display titles keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Title
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets the initial sort direction (<c>asc</c> or <c>desc</c>).
    /// </summary>
    public string Order { get; set; } = "desc";
}

/// <summary>
/// Associates a mod entry with categories, labels, keywords, and sort metric values declared by its repository.
/// </summary>
public sealed record ModIndexItemMetadata
{
    /// <summary>
    /// Gets the optional category ID declared in the owning index's metadata.
    /// </summary>
    public string? Category { get; init; }

    /// <summary>
    /// Gets or sets label IDs declared in the owning index's metadata.
    /// </summary>
    public string[] Labels { get; set; } = [];

    /// <summary>
    /// Gets or sets free-form search keywords for client discovery.
    /// </summary>
    public string[] Keywords { get; set; } = [];

    /// <summary>
    /// Gets or sets numeric sort values mapped by declared sort key ID; <see langword="null"/> indicates unavailable metrics.
    /// </summary>
    public Dictionary<string, double?> SortValues { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Represents one logical mod listed inside a static repository index.
/// </summary>
public sealed record ModIndexEntry
{
    /// <summary>
    /// Gets the unique stable mod identifier matching <see cref="ModManifest.Id"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets or sets localized display names keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Name
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized short descriptions keyed by BCP-47 language tag.
    /// </summary>
    public Dictionary<string, string> Description
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the optional author or publisher display name.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the optional inline <c>data:</c> URI (max 32 KiB) or relative/HTTPS icon URL.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Gets the optional HTTP(S) web URL describing this mod.
    /// </summary>
    public string? Homepage { get; init; }

    /// <summary>
    /// Gets the optional source repository metadata for display purposes.
    /// </summary>
    public ModRepositoryInfo? Repository { get; init; }

    /// <summary>
    /// Gets the optional license summary declared for this mod.
    /// </summary>
    public ModIndexLicense? License { get; init; }

    /// <summary>
    /// Gets or sets repository taxonomy tags and sort metrics for this mod.
    /// </summary>
    public ModIndexItemMetadata Metadata { get; set; } = new();

    /// <summary>
    /// Gets or sets whether built-in or repository-recommended installations enable this mod by default.
    /// </summary>
    public bool IsDefaultEnabled { get; set; }

    /// <summary>
    /// Gets or sets named distribution channels (such as <c>stable</c> or <c>preview</c>) mapped to exact listed versions.
    /// </summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the available packaged versions of this mod.
    /// </summary>
    public required ModIndexVersion[] Versions { get; init; }
}

/// <summary>
/// Represents one installable version of a mod inside a static repository index.
/// </summary>
public sealed record ModIndexVersion
{
    /// <summary>
    /// Gets the exact semantic version matching the packaged manifest.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets the publish timestamp in Unix seconds.
    /// </summary>
    public long PublishedAt { get; init; }

    /// <summary>
    /// Gets or sets localized Markdown documentation references relative to the index URL (or absolute HTTPS URLs).
    /// </summary>
    public Dictionary<string, string> Readme
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the optional relative or HTTPS URL for the full license text document.
    /// </summary>
    public string? LicenseUrl { get; init; }

    /// <summary>
    /// Gets or sets optional relative or HTTPS URLs for bundled third-party license notices.
    /// </summary>
    public string[] ThirdPartyLicenseUrls { get; set; } = [];

    /// <summary>
    /// Gets the entry declaration (universal or per-platform) copied directly from <c>mod.json</c>.
    /// </summary>
    public required ModEntry Entry { get; init; }

    /// <summary>
    /// Gets the downloadable ZIP archive location, integrity digest, and optional signature.
    /// </summary>
    public required ModArtifact Artifact { get; init; }
}

/// <summary>
/// Describes a downloadable mod package ZIP archive and its cryptographic verification metadata.
/// </summary>
public sealed record ModArtifact
{
    /// <summary>
    /// Gets the archive URL, typically relative to the containing <c>index.json</c>.
    /// </summary>
    public required string Url { get; init; }

    /// <summary>
    /// Gets the exact archive byte length used for download bounds and verification.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Gets the expected lowercase hexadecimal SHA-256 digest of the ZIP archive.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>
    /// Gets the optional ECDSA P-256 signature binding mod ID, version, digest, and byte length.
    /// </summary>
    public string? Signature { get; init; }
}

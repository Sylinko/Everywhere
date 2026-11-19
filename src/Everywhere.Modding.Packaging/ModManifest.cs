namespace Everywhere.Modding.Packaging;

/// <summary>
/// Describes one logical mod package. The SDK generates this file from author intent and build outputs.
/// </summary>
public sealed record ModManifest
{
    /// <summary>
    /// Gets or sets the supported serialized schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets the stable mod identifier, independent of assembly names.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the exact package version shared by all platform implementations.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets or sets localized display names; an empty map leaves the mod ID as its display name.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Name
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized short descriptions, independent of platform entries.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Description
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized package-relative Markdown paths, readable without loading mod code.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Readme
    {
        get;
        set => field = ModValidationRules.NormalizeTranslations(value);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the optional author or publisher display name.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets the optional package-relative icon image path (<c>.svg</c>, <c>.png</c>, <c>.jpg</c>, <c>.jpeg</c>, or <c>.webp</c>).
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
    /// Gets the optional license and third-party notice metadata declared for this package.
    /// </summary>
    public ModLicense? License { get; init; }

    /// <summary>
    /// Gets the universal or platform-selected entry declaration.
    /// </summary>
    public required ModEntry Entry { get; init; }
}

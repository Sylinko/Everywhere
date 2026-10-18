namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Describes one logical plugin package. The SDK generates this file from author intent and build outputs.
/// </summary>
public sealed record PluginManifest
{
    /// <summary>
    /// Gets or sets the supported serialized schema version.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>
    /// Gets the stable plugin identifier, independent of assembly names.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the exact package version shared by all platform implementations.
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// Gets or sets localized display names; an empty map leaves the plugin ID as its display name.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Name
    {
        get;
        set => field = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized short descriptions, independent of platform entries.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Description
    {
        get;
        set => field = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets or sets localized package-relative Markdown paths, readable without loading plugin code.
    /// Language tags compare without case; values retain their original spelling.
    /// </summary>
    public Dictionary<string, string> Readme
    {
        get;
        set => field = new Dictionary<string, string>(value, StringComparer.OrdinalIgnoreCase);
    } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the universal or platform-selected entry declaration.
    /// </summary>
    public required PluginEntry Entry { get; init; }
}
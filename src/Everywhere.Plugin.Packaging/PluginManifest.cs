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
    /// Gets the universal or platform-selected entry declaration.
    /// </summary>
    public required PluginEntry Entry { get; init; }
}
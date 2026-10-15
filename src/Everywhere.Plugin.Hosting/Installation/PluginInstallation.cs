using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting.Installation;

/// <summary>
/// Identifies a complete on-disk import without claiming that it has been activated.
/// </summary>
public sealed record PluginInstallation
{
    /// <summary>
    /// Gets the host-generated identifier distinguishing same-version builds.
    /// </summary>
    public required string InstallationId { get; init; }

    /// <summary>
    /// Gets the final immutable-by-contract directory containing plugin.json.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Gets the validated package declaration.
    /// </summary>
    public required PluginManifest Manifest { get; init; }
}
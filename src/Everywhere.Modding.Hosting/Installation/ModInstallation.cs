using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Installation;

/// <summary>
/// Identifies a complete on-disk import without claiming that it has been activated.
/// </summary>
public sealed record ModInstallation
{
    /// <summary>
    /// Gets the host-generated identifier distinguishing same-version builds.
    /// </summary>
    public required string InstallationId { get; init; }

    /// <summary>
    /// Gets the final immutable-by-contract directory containing mod.json.
    /// </summary>
    public required string Directory { get; init; }

    /// <summary>
    /// Gets the validated package declaration.
    /// </summary>
    public required ModManifest Manifest { get; init; }
}
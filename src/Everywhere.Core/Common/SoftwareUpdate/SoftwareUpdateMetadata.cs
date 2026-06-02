using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Common;

/// <summary>
/// Represents metadata about an available software update, including version,
/// release date, release notes, download asset, and download state.
/// </summary>
public sealed partial class SoftwareUpdateMetadata(
    SemanticVersion version,
    DateTimeOffset publishedAt,
    string? releaseNotes,
    SoftwareUpdateMetadata.UpdateAsset? asset
) : ObservableObject
{
    /// <summary>
    /// The latest version available for update.
    /// </summary>
    public SemanticVersion Version { get; } = version;

    /// <summary>
    /// The publication date of the release.
    /// </summary>
    public DateTimeOffset PublishedAt { get; } = publishedAt;

    /// <summary>
    /// The release notes (Markdown body) from the GitHub release, or <see langword="null"/> if unavailable.
    /// </summary>
    public string? ReleaseNotes { get; } = releaseNotes;

    /// <summary>
    /// The download asset associated with this update.
    /// </summary>
    public UpdateAsset? Asset { get; } = asset;

    /// <summary>
    /// Gets the download progress as a normalized value (0.0 to 1.0).
    /// </summary>
    [ObservableProperty]
    public partial double DownloadProgress { get; set; }

    /// <summary>
    /// Gets whether the asset has been fully downloaded and is ready to install.
    /// </summary>
    [ObservableProperty]
    public partial bool IsReady { get; set; }

    /// <summary>
    /// Represents a downloadable asset (installer or archive) for a release.
    /// </summary>
    public sealed record UpdateAsset(
        string Name,
        string Digest,
        long Size,
        string ProxyDownloadUrl,
        string DirectDownloadUrl
    );
}

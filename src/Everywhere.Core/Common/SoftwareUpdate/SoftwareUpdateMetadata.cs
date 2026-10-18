namespace Everywhere.Common;

/// <summary>
/// Identifies one immutable update release and its selected platform asset.
/// </summary>
/// <param name="Channel">
/// The release channel encoded by the semantic version.
/// </param>
/// <param name="Tag">
/// The complete release tag used by fixed-detail and download requests.
/// </param>
/// <param name="Version">
/// The parsed semantic version of the release.
/// </param>
/// <param name="PublishedAt">
/// The release publication time used by automatic-download eligibility policy.
/// </param>
/// <param name="Asset">
/// The platform-specific package selected for this release.
/// </param>
public sealed record SoftwareUpdateMetadata(
    UpdateChannel Channel,
    string Tag,
    SemanticVersion Version,
    DateTimeOffset PublishedAt,
    SoftwareUpdateMetadata.UpdateAsset Asset)
{
    /// <summary>
    /// Determines whether another metadata snapshot identifies the same downloadable package.
    /// </summary>
    /// <param name="other">
    /// The metadata snapshot to compare.
    /// </param>
    /// <returns>
    /// <see langword="true"/> when the release and package identity fields match; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    internal bool HasSamePackageIdentity(SoftwareUpdateMetadata? other)
    {
        return other is not null &&
               Channel == other.Channel &&
               Version == other.Version &&
               string.Equals(Tag, other.Tag, StringComparison.Ordinal) &&
               string.Equals(Asset.Name, other.Asset.Name, StringComparison.Ordinal) &&
               Asset.Size == other.Asset.Size &&
               string.Equals(Asset.Digest, other.Asset.Digest, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Represents the selected downloadable asset for a release.
    /// </summary>
    /// <param name="Name">
    /// The package file name.
    /// </param>
    /// <param name="Digest">
    /// The package digest supplied by the release service.
    /// </param>
    /// <param name="Size">
    /// The expected package size in bytes.
    /// </param>
    /// <param name="ProxyDownloadUrl">
    /// The fixed-version download-center URL used as the primary source.
    /// </param>
    /// <param name="DirectDownloadUrl">
    /// The fixed-tag upstream URL used as the fallback source.
    /// </param>
    public sealed record UpdateAsset(
        string Name,
        string Digest,
        long Size,
        string ProxyDownloadUrl,
        string DirectDownloadUrl);
}

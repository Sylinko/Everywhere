namespace Everywhere.Common;

/// <summary>
/// Supplies releases for platforms whose package manager owns installation.
/// </summary>
public interface ITerminalUpdateHandler
{
    /// <summary>
    /// Reads published releases for the requested channel without downloading packages.
    /// </summary>
    Task<IReadOnlyList<TerminalUpdateRelease>> GetReleasesAsync(UpdateChannel channel, CancellationToken cancellationToken);
}

/// <summary>
/// A published release with its update identity and release notes.
/// </summary>
public sealed record TerminalUpdateRelease(SoftwareUpdateMetadata Metadata, Uri ReleaseUri, string Notes);
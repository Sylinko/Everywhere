using System.ComponentModel;

namespace Everywhere.Common;

public interface ISoftwareUpdater : INotifyPropertyChanged
{
    /// <summary>
    /// Gets a value indicating whether an update is available.
    /// </summary>
    DateTimeOffset? LastCheckTime { get; }

    /// <summary>
    /// Gets the full metadata for the latest available update, or <see langword="null"/> if none.
    /// Includes version, publication date, release notes, download asset, and download state.
    /// </summary>
    SoftwareUpdateMetadata? LatestUpdate { get; }

    /// <summary>
    /// Gets whether the update asset is currently being downloaded in the background.
    /// </summary>
    bool IsDownloading { get; }

    /// <summary>
    /// Runs the automatic update check in the background.
    /// </summary>
    void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Manually checks for updates asynchronously.
    /// </summary>
    /// <param name="throwOnError"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the update process.
    /// </summary>
    /// <param name="progress">a 0-1 progress indicator for the update process</param>
    /// <param name="cancellationToken"></param>
    Task PerformUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

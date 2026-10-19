using System.ComponentModel;

namespace Everywhere.Common;

/// <summary>
/// Coordinates update discovery, package ownership, automatic downloading, and manual installation.
/// </summary>
public interface ISoftwareUpdater : INotifyPropertyChanged
{
    /// <summary>
    /// Gets the effective channel used for update discovery and package selection.
    /// </summary>
    UpdateChannel UpdateChannel { get; }

    /// <summary>
    /// Gets the completion time of the last successful latest-release check.
    /// </summary>
    DateTimeOffset? LastCheckTime { get; }

    /// <summary>
    /// Gets the latest applicable release discovered for <see cref="UpdateChannel"/>.
    /// </summary>
    /// <remarks>
    /// This value is <see langword="null"/> when the latest release is not newer than the running version
    /// or no successful discovery has completed for the effective channel.
    /// </remarks>
    SoftwareUpdateMetadata? LatestUpdate { get; }

    /// <summary>
    /// Gets the release whose package is present locally and has passed package validation.
    /// </summary>
    /// <remarks>
    /// A recovered or automatically downloaded package may be ready even when its identity has not yet
    /// been confirmed against a fresh latest-release check.
    /// </remarks>
    SoftwareUpdateMetadata? ReadyUpdate { get; }

    /// <summary>
    /// Gets whether <see cref="ReadyUpdate"/> was confirmed by a latest-release check performed after download.
    /// </summary>
    bool IsReadyUpdateConfirmedLatest { get; }

    /// <summary>
    /// Gets the release presented by compact update entry points.
    /// </summary>
    /// <remarks>
    /// Active work presents its fixed target. Otherwise the latest discovered release takes precedence,
    /// with a validated ready package or a failed/cancelled target available as a fallback.
    /// </remarks>
    SoftwareUpdateMetadata? DisplayUpdate { get; }

    /// <summary>
    /// Gets the latest immutable snapshot of coordinator-owned update work.
    /// </summary>
    SoftwareUpdateOperation? CurrentOperation { get; }

    /// <summary>
    /// Gets whether an update action or active operation should be exposed by compact update entry points.
    /// </summary>
    bool IsUpdateActionVisible { get; }

    /// <summary>
    /// Starts periodic latest-release checks and automatic-download policy evaluation.
    /// </summary>
    /// <param name="interval">
    /// The interval between periodic checks.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that stops the automatic workflow.
    /// </param>
    void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores a validated cached update package, if one was persisted by a previous session.
    /// </summary>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for recovery. Recovery itself continues independently.
    /// </param>
    Task RecoverCachedPackageAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks the effective channel for its latest applicable release.
    /// </summary>
    /// <param name="throwOnError">
    /// Whether a discovery failure should be propagated to the caller after it is logged.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels waiting for the check.
    /// </param>
    Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one exact release and its platform asset without replacing global latest-discovery state.
    /// </summary>
    /// <param name="channel">
    /// The channel that must own the release.
    /// </param>
    /// <param name="fullTag">
    /// The complete release tag returned by the version-history service.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels the request.
    /// </param>
    /// <returns>
    /// Immutable metadata for the requested release and its selected platform asset.
    /// </returns>
    Task<SoftwareUpdateMetadata> ResolveUpdateAsync(
        UpdateChannel channel,
        string fullTag,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or joins the manual update operation selected from current discovery and validated cache state.
    /// </summary>
    /// <remarks>
    /// The channel and any known target are captured when the operation starts. A running automatic
    /// operation cannot be promoted to installation; cancel it or wait for it to finish first.
    /// </remarks>
    /// <param name="cancellationToken">
    /// A token that cancels this caller's wait without canceling shared update work.
    /// </param>
    Task PerformUpdateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts or joins a manual update operation for an explicitly resolved release.
    /// </summary>
    /// <param name="target">
    /// The exact release and platform asset to download or install.
    /// </param>
    /// <param name="cancellationToken">
    /// A token that cancels this caller's wait without canceling shared update work.
    /// </param>
    Task PerformUpdateAsync(SoftwareUpdateMetadata target, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of the current cancellable update operation.
    /// </summary>
    /// <remarks>
    /// Checking, downloading, and validation are cancellable regardless of how the operation started.
    /// Cancellation suppresses automatic downloading of that release for this application session;
    /// an explicit manual retry clears its suppression. Installation is not cancellable.
    /// </remarks>
    void CancelCurrentOperation();
}
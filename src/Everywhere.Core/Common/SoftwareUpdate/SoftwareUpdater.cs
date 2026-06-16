using System.Diagnostics;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
#if !DEBUG
using Everywhere.Utilities;
#endif

namespace Everywhere.Common;

public sealed partial class SoftwareUpdater(
    Settings settings,
    IPlatformUpdateHandler platformHandler,
    IHttpClientFactory httpClientFactory,
    IFileDownloadService fileDownloadService,
    INativeHelper nativeHelper,
    ILogger<SoftwareUpdater> logger
) : ObservableObject, ISoftwareUpdater, IDisposable
{
    private const string UpdateServiceBaseUrl = "https://download.sylinko.com";
    private const string GitHubDirectUrlBase = "https://github.com/Sylinko/Everywhere/releases/download";

    public UpdateChannel UpdateChannel => settings.Common.UpdateChannel switch
    {
        UpdateChannel.Unknown => RuntimeConstants.Version.Channel switch
        {
            UpdateChannel.Unknown => UpdateChannel.Stable,
            _ => RuntimeConstants.Version.Channel
        },
        _ => settings.Common.UpdateChannel
    };

    private string DownloadUrlBase => $"{UpdateServiceBaseUrl}/download?product=everywhere&os={platformHandler.OsIdentifier}&channel={UpdateChannel}";

    /// <summary>
    /// Minimum age of a release before auto-download is triggered (24 hours).
    /// Prevents automatic distribution of releases that may have critical regressions discovered shortly after publish.
    /// </summary>
    private static readonly TimeSpan MinReleaseAgeForAutoDownload = TimeSpan.FromHours(24);

    /// <summary>
    /// Auto-download bandwidth cap in bytes per second (1 MB/s).
    /// </summary>
    private const long AutoDownloadBytesPerSecond = 1024 * 1024;

    /// <summary>
    /// Maximum number of consecutive auto-download retries before waiting for the next check cycle.
    /// </summary>
    private const int MaxAutoDownloadRetries = 3;

    private readonly ActivitySource _activitySource = new(typeof(SoftwareUpdater).FullName.NotNull());

#if !DEBUG
    private PeriodicTimer? _timer;
#endif

    private Task? _updateTask;
    private Task? _autoDownloadTask;
    private int _autoDownloadRetryCount;

    [ObservableProperty]
    public partial DateTimeOffset? LastCheckTime { get; private set; }

    [ObservableProperty]
    public partial SoftwareUpdateMetadata? LatestUpdate { get; private set; }

    [ObservableProperty]
    public partial bool IsDownloading { get; private set; }

    public void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default)
    {
#if !DEBUG
        PeriodicTimer timer;
        _timer = timer = new PeriodicTimer(interval);
        cancellationToken.Register(Stop);

        Task.Run(
            async () =>
            {
                // Clean up old update packages on startup.
                CleanupOldUpdates();
                await CheckForUpdatesAsync(false, cancellationToken); // check immediately on start

                try
                {
                    while (await timer.WaitForNextTickAsync(cancellationToken))
                    {
                        await CheckForUpdatesAsync(false, cancellationToken);
                    }
                }
                catch (OperationCanceledException) { }
            },
            cancellationToken);

        void Stop()
        {
            DisposeHelper.DisposeToDefault(ref _timer);
        }
#endif
    }

    public async Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_updateTask is not null) return;

            using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
            var targetChannel = UpdateChannel;
            var response = await httpClient.GetAsync($"{UpdateServiceBaseUrl}/api?product=everywhere&channel={targetChannel}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var jsonDoc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = jsonDoc.RootElement;

            var latestTag = root.GetProperty("tag_name").GetString();
            if (latestTag is null) return;

            var versionString = latestTag.StartsWith('v') ? latestTag[1..] : latestTag;
            if (!SemanticVersion.TryParse(versionString, out var latestVersion))
            {
                logger.LogWarning("Could not parse version from tag: {Tag}", latestTag);
                return;
            }

            if (latestVersion.Channel != targetChannel)
            {
                logger.LogInformation(
                    "Skipping update {RemoteVersion} from channel {RemoteChannel} because the selected update channel is {TargetChannel}.",
                    latestVersion,
                    latestVersion.Channel,
                    targetChannel);
                LatestUpdate = null;
                return;
            }

            // Only proceed if the remote version is actually newer.
            if (latestVersion <= RuntimeConstants.Version)
            {
                LatestUpdate = null;
                return;
            }

            // Parse release metadata.
            var publishedAt = root.TryGetProperty("published_at", out var publishedAtElement) &&
                publishedAtElement.TryGetDateTimeOffset(out var date) ?
                    date :
                    DateTimeOffset.UtcNow;
            var releaseNotes = root.TryGetProperty("body", out var bodyElement) ? bodyElement.GetString() : null;
            var assets = root.GetProperty("assets").Deserialize(UpdateAssetMetadataJsonSerializerContext.Default.ListUpdateAssetMetadata);

            SoftwareUpdateMetadata.UpdateAsset? asset = null;
            var assetMetadata = platformHandler.SelectAsset(assets ?? [], versionString);
            if (assetMetadata is not null)
            {
                asset = new SoftwareUpdateMetadata.UpdateAsset(
                    assetMetadata.Name,
                    assetMetadata.Digest,
                    assetMetadata.Size,
                    $"{DownloadUrlBase}&type={platformHandler.GetDownloadType()}",
                    $"{GitHubDirectUrlBase}/{latestTag}/{assetMetadata.Name}"
                );
            }

            LatestUpdate = new SoftwareUpdateMetadata(latestVersion, publishedAt, releaseNotes, asset);

            // Trigger background auto-download if conditions are met.
            TriggerAutoDownloadIfAppropriate();
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Failed to check for updates.");
            LatestUpdate = null;

            if (throwOnError) throw;
        }
        finally
        {
            LastCheckTime = DateTimeOffset.UtcNow;
        }
    }

    public async Task PerformUpdateAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_updateTask is not null)
        {
            await _updateTask;
            return;
        }

        if (LatestUpdate is not { Asset: { } asset })
        {
            logger.LogDebug("No new version available to update.");
            return;
        }

        _updateTask = Task.Run(
            async () =>
            {
                using var activity = _activitySource.StartActivity();

                try
                {
                    var assetPath = await DownloadAssetAsync(asset, progress, cancellationToken);
                    await platformHandler.ExecuteUpdateAsync(assetPath, cancellationToken);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    ex = new HandledException(ex, new DynamicLocaleKey(LocaleKey.SoftwareUpdater_PerformUpdate_FailedToast_Message));
                    activity?.SetStatus(ActivityStatusCode.Error, ex.Message);

                    logger.LogError(ex, "Failed to perform update.");
                    throw;
                }
                finally
                {
                    _updateTask = null;
                }
            },
            cancellationToken);

        await _updateTask;
    }

#if !DEBUG
    /// <summary>
    /// Cleans up old update packages from the updates directory.
    /// </summary>
    private void CleanupOldUpdates()
    {
        try
        {
            var updatesPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
            if (!Directory.Exists(updatesPath)) return;

            foreach (var file in Directory.EnumerateFiles(updatesPath))
            {
                var fileName = Path.GetFileName(file);

                if (!platformHandler.TryParseUpdatePackageVersion(fileName, out var fileVersion) || fileVersion is null) continue;

                // Delete if the package version is older than or same as the current running version.
                if (fileVersion > RuntimeConstants.Version) continue;

                try
                {
                    File.Delete(file);
                    logger.LogDebug("Deleted old update package: {FileName}", fileName);
                }
                catch (Exception ex)
                {
                    ex = HandledSystemException.Handle(ex);
                    logger.LogInformation(ex, "Failed to delete old update package: {FileName}", fileName);
                }
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "An error occurred during old updates cleanup.");
        }
    }
#endif

    /// <summary>
    /// Triggers automatic background download if: not in low-data mode, release is old enough,
    /// and no download is already in progress or has exceeded max retries.
    /// </summary>
    private void TriggerAutoDownloadIfAppropriate()
    {
        if (LatestUpdate is not { Asset: { } asset }) return;
        if (nativeHelper.IsLowDataModeActive) return;

        var releaseAge = DateTimeOffset.UtcNow - LatestUpdate.PublishedAt;
        if (releaseAge < MinReleaseAgeForAutoDownload)
        {
            logger.LogDebug(
                "Auto-download skipped: release is too recent (age: {Age:h\\:mm}, min: {Min:h\\:mm}).",
                releaseAge,
                MinReleaseAgeForAutoDownload);
            return;
        }

        if (_autoDownloadTask is { IsCompleted: false })
        {
            logger.LogDebug("Auto-download already in progress.");
            return;
        }

        if (_autoDownloadRetryCount >= MaxAutoDownloadRetries)
        {
            logger.LogDebug("Auto-download retry limit reached ({Count}).", _autoDownloadRetryCount);
            return;
        }

        _autoDownloadTask = Task.Run(() => AutoDownloadWithRetryAsync(asset));
        _autoDownloadTask.Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    /// <summary>
    /// Performs the auto-download with exponential backoff on failure.
    /// </summary>
    private async Task AutoDownloadWithRetryAsync(SoftwareUpdateMetadata.UpdateAsset asset)
    {
        try
        {
            IsDownloading = true;
            var progress = new Progress<double>(p => LatestUpdate?.DownloadProgress = p);

            await DownloadAssetAsync(asset, progress, CancellationToken.None);
            _autoDownloadRetryCount = 0;
            LatestUpdate?.IsReady = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Auto-download attempt {Attempt} failed.", _autoDownloadRetryCount + 1);

            _autoDownloadRetryCount++;

            if (_autoDownloadRetryCount < MaxAutoDownloadRetries)
            {
                // Exponential backoff: 1h, 2h, 4h.
                var delay = TimeSpan.FromHours(Math.Pow(2, _autoDownloadRetryCount - 1));
                logger.LogInformation("Retrying auto-download in {Delay}.", delay);
                await Task.Delay(delay);

                // Only retry if we still have the same update target.
                if (LatestUpdate?.Asset is not null)
                {
                    TriggerAutoDownloadIfAppropriate();
                }
            }
        }
        finally
        {
            IsDownloading = false;
        }
    }

    private async Task<string> DownloadAssetAsync(
        SoftwareUpdateMetadata.UpdateAsset asset,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var installPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
        var assetDownloadPath = Path.Combine(installPath, asset.Name);

        return await fileDownloadService.DownloadAsync(
            new FileDownloadRequest(
                assetDownloadPath,
                [
                    new FileDownloadSource(asset.DirectDownloadUrl, "GitHub"),
                    new FileDownloadSource(asset.ProxyDownloadUrl, "Proxy")
                ],
                asset.Size,
                asset.Digest,
                AutoDownloadBytesPerSecond),
            progress,
            cancellationToken);
    }

    public void Dispose()
    {
#if !DEBUG
        _timer?.Dispose();
#endif
    }
}

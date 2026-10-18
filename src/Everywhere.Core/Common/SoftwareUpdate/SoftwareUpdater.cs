using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Everywhere.Common;

/// <summary>
/// Owns update discovery, download, validation, cancellation, and installation.
/// All callers share the same download owner, so two execution tasks never write the same package concurrently.
/// </summary>
public sealed class SoftwareUpdater(
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
    private const string PackageManifestFileName = "update-package.json";
    private const long AutoDownloadBytesPerSecond = 1024 * 1024;
    private const int MaxAutoDownloadRetries = 3;

    private static readonly TimeSpan MinReleaseAgeForAutoDownload = TimeSpan.FromHours(1);

    public UpdateChannel UpdateChannel => settings.Common.UpdateChannel switch
    {
        UpdateChannel.Unknown => RuntimeConstants.Version.Channel switch
        {
            UpdateChannel.Unknown => UpdateChannel.Stable,
            _ => RuntimeConstants.Version.Channel
        },
        _ => settings.Common.UpdateChannel
    };

    public DateTimeOffset? LastCheckTime
    {
        get
        {
            lock (_gate)
            {
                return _lastCheckTime;
            }
        }
    }

    public SoftwareUpdateMetadata? LatestUpdate
    {
        get
        {
            lock (_gate)
            {
                return _latestUpdate?.Channel == UpdateChannel ? _latestUpdate : null;
            }
        }
    }

    public SoftwareUpdateMetadata? ReadyUpdate
    {
        get
        {
            lock (_gate)
            {
                return _readyPackage?.Metadata.Channel == UpdateChannel ? _readyPackage.Metadata : null;
            }
        }
    }

    public bool IsReadyUpdateConfirmedLatest
    {
        get
        {
            lock (_gate)
            {
                return _readyPackage is { IsConfirmedLatest: true } readyPackage && readyPackage.Metadata.Channel == UpdateChannel;
            }
        }
    }

    public SoftwareUpdateMetadata? DisplayUpdate
    {
        get
        {
            lock (_gate)
            {
                if (_latestUpdate?.Channel == UpdateChannel) return _latestUpdate;
                return _readyPackage?.Metadata.Channel == UpdateChannel ? _readyPackage.Metadata : null;
            }
        }
    }

    public SoftwareUpdateOperation? CurrentOperation
    {
        get
        {
            lock (_gate) return _currentOperation;
        }
    }

    public bool IsUpdateActionVisible => DisplayUpdate is not null || CurrentOperation?.IsActive == true;

    private readonly Lock _gate = new();
    private readonly Lock _manifestGate = new();
    private readonly ActivitySource _activitySource = new(typeof(SoftwareUpdater).FullName.NotNull());
    private readonly CancellationTokenSource _disposeCancellationTokenSource = new();

    private DateTimeOffset? _lastCheckTime;
    private SoftwareUpdateMetadata? _latestUpdate;
    private ReadyPackage? _readyPackage;
    private SoftwareUpdateOperation? _currentOperation;
    private ActiveDownload? _activeDownload;
    private ManualOperation? _manualOperation;
    private Task<SoftwareUpdateMetadata?>? _latestCheckTask;
    private Task? _recoveryTask;
#if !DEBUG
    private Task? _automaticLoopTask;
    private CancellationTokenSource? _automaticLoopCancellationTokenSource;
#endif
    private Task? _automaticDownloadWorkflow;
    private CancellationTokenSource? _eligibilityCancellationTokenSource;
    private CancellationTokenSource? _retryCancellationTokenSource;
    private bool _automaticEnabled;
    private long _nextOperationId;
    private int _autoDownloadRetryCount;
    private string? _autoDownloadRetryTag;
    private string? _suppressedAutomaticDownloadTag;
    private bool _disposed;

    public void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default)
    {
#if !DEBUG
        lock (_gate)
        {
            if (_disposed || _automaticLoopTask is { IsCompleted: false }) return;

            _automaticEnabled = true;
            _automaticLoopCancellationTokenSource?.Dispose();
            _automaticLoopCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellationTokenSource.Token);
            var token = _automaticLoopCancellationTokenSource.Token;
            _automaticLoopTask = Task.Run(() => RunAutomaticLoopAsync(interval, token), token);
            _automaticLoopTask.Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }
#endif
    }

    public async Task HandleUpdateChannelChangedAsync(CancellationToken cancellationToken = default)
    {
        ActiveDownload? obsoleteAutomaticDownload = null;
        var channel = UpdateChannel;

        ClearAutomaticDownloadSuppression();

        lock (_gate)
        {
            if (_latestUpdate?.Channel != channel) _latestUpdate = null;
            if (_activeDownload is { Intent: SoftwareUpdateOperationIntent.Automatic } activeDownload &&
                activeDownload.Target.Channel != channel)
            {
                obsoleteAutomaticDownload = activeDownload;
                activeDownload.CancellationTokenSource.Cancel();
            }
        }

        CancelEligibilityTimer();
        NotifyPropertyChanged(nameof(UpdateChannel));
        NotifyDiscoveryChanged();
        NotifyReadyPackageChanged();

        if (obsoleteAutomaticDownload is not null)
            await IgnoreCancellationAndFailureAsync(obsoleteAutomaticDownload.Task);

#if !DEBUG
        // Changing a setting must not start discovery in a Debug session.
        // Explicit checks and release-page navigation remain available in both builds.
        try
        {
            await EnsureRecoveryAsync(cancellationToken);
            await GetLatestUpdateAsync(forceFresh: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Failed to refresh updates after the update channel changed.");
        }
#endif
    }

    public async Task RecoverCachedPackageAsync(CancellationToken cancellationToken = default)
    {
        await EnsureRecoveryAsync(cancellationToken);
    }

    public async Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default)
    {
        try
        {
            ClearAutomaticDownloadSuppression();
            await EnsureRecoveryAsync(cancellationToken);
            await GetLatestUpdateAsync(forceFresh: false, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Failed to check for updates.");
            if (throwOnError) throw ex;
        }
    }

    public async Task<SoftwareUpdateMetadata> ResolveUpdateAsync(
        UpdateChannel channel,
        string fullTag,
        CancellationToken cancellationToken = default)
    {
        Debug.Assert(!fullTag.IsNullOrWhiteSpace());

        await EnsureRecoveryAsync(cancellationToken);
        var metadata = await FetchReleaseAsync(channel, fullTag, cancellationToken);
        Debug.Assert(string.Equals(metadata.Tag, fullTag, StringComparison.Ordinal));

        if (channel != UpdateChannel)
        {
            throw new OperationCanceledException("The selected update channel changed while resolving the release.");
        }

        return metadata;
    }

    public Task PerformUpdateAsync(CancellationToken cancellationToken = default) =>
        StartOrJoinManualOperationAsync(null, cancellationToken);

    public Task PerformUpdateAsync(SoftwareUpdateMetadata target, CancellationToken cancellationToken = default)
    {
        return StartOrJoinManualOperationAsync(target, cancellationToken);
    }

    public void CancelCurrentOperation()
    {
        CancellationTokenSource? cancellationTokenSource = null;
        bool notify = false;

        lock (_gate)
        {
            var current = _currentOperation;
            if (current is not { CanCancel: true }) return;

            if (_manualOperation is { } manualOperation)
            {
                _currentOperation = new SoftwareUpdateOperation(
                    manualOperation.Id,
                    SoftwareUpdateOperationIntent.Manual,
                    SoftwareUpdateOperationStage.Cancelling,
                    current.Target,
                    current.Progress);
                cancellationTokenSource = manualOperation.CancellationTokenSource;
            }
            else if (current.Intent == SoftwareUpdateOperationIntent.Automatic &&
                     _activeDownload is { Intent: SoftwareUpdateOperationIntent.Automatic } download &&
                     download.OperationId == current.Id)
            {
                _currentOperation = new SoftwareUpdateOperation(
                    download.OperationId,
                    SoftwareUpdateOperationIntent.Automatic,
                    SoftwareUpdateOperationStage.Cancelling,
                    current.Target,
                    current.Progress);
                _suppressedAutomaticDownloadTag = download.Target.Tag;
                cancellationTokenSource = download.CancellationTokenSource;
            }

            notify = cancellationTokenSource is not null;
        }

        if (!notify) return;

        NotifyOperationChanged();
        try
        {
            cancellationTokenSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owner may have completed between the state change and this request.
        }
    }

    private async Task RunAutomaticLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureRecoveryAsync(cancellationToken);
            CleanupOldUpdates();
            await RunAutomaticCheckSafelyAsync(cancellationToken);

            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await RunAutomaticCheckSafelyAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Automatic update checking stopped unexpectedly.");
        }
        finally
        {
            StopAutomaticWork();
        }
    }

    private void ClearAutomaticDownloadSuppression()
    {
        lock (_gate)
        {
            _suppressedAutomaticDownloadTag = null;
        }
    }

    private async Task RunAutomaticCheckSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            ClearAutomaticDownloadSuppression();
            await GetLatestUpdateAsync(forceFresh: true, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Automatic update check failed.");
        }
    }

    private async Task<SoftwareUpdateMetadata?> GetLatestUpdateAsync(bool forceFresh, CancellationToken cancellationToken)
    {
        Task<SoftwareUpdateMetadata?>? precedingTask = null;
        Task<SoftwareUpdateMetadata?>? sharedTask = null;

        lock (_gate)
        {
            if (_latestCheckTask is { IsCompleted: false } currentTask)
            {
                if (forceFresh)
                    precedingTask = currentTask;
                else
                    sharedTask = currentTask;
            }
        }

        if (sharedTask is not null)
        {
            return await sharedTask.WaitAsync(cancellationToken);
        }

        if (precedingTask is not null)
        {
            try
            {
                await precedingTask.WaitAsync(cancellationToken);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        Task<SoftwareUpdateMetadata?> task;
        var channel = UpdateChannel;
        lock (_gate)
        {
            if (_latestCheckTask is { IsCompleted: false } currentTask)
            {
                task = currentTask;
            }
            else
            {
                task = Task.Run(() => CheckLatestUpdateCoreAsync(channel, _disposeCancellationTokenSource.Token));
                _latestCheckTask = task;
            }
        }

        return await task.WaitAsync(cancellationToken);
    }

    private async Task<SoftwareUpdateMetadata?> CheckLatestUpdateCoreAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        try
        {
            var metadata = await FetchReleaseAsync(channel, null, cancellationToken);
            var applicableUpdate = metadata.Version > RuntimeConstants.Version ? metadata : null;
            await ApplySuccessfulDiscoveryAsync(channel, applicableUpdate);
            return applicableUpdate;
        }
        finally
        {
            lock (_gate)
            {
                _latestCheckTask = null;
            }
        }
    }

    private async Task<SoftwareUpdateMetadata> FetchReleaseAsync(
        UpdateChannel channel,
        string? fullTag,
        CancellationToken cancellationToken)
    {
        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
        var requestUrl = $"{UpdateServiceBaseUrl}/api?product=everywhere&channel={Uri.EscapeDataString(channel.ToString())}";
        if (fullTag is not null) requestUrl += $"&version={Uri.EscapeDataString(fullTag)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (fullTag is null) request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (fullTag is not null && response.StatusCode == HttpStatusCode.NotFound)
            throw new SoftwareUpdateConsistencyException($"Release detail for {fullTag} was not found.");
        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var jsonDocument = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
        var root = jsonDocument.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (tag.IsNullOrWhiteSpace()) throw new InvalidDataException("Release detail is missing tag_name.");
        if (fullTag is not null && !string.Equals(tag, fullTag, StringComparison.Ordinal))
            throw new SoftwareUpdateConsistencyException($"Release detail returned {tag} instead of requested tag {fullTag}.");

        var versionString = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!SemanticVersion.TryParse(versionString, out var version))
            throw new InvalidDataException($"Could not parse version from release tag {tag}.");
        if (version.Channel != channel)
            throw new SoftwareUpdateConsistencyException(
                $"Release {tag} belongs to {version.Channel}, not requested channel {channel}.");
        var publishedAt = root.TryGetProperty("published_at", out var publishedAtElement) &&
            publishedAtElement.TryGetDateTimeOffset(out var parsedPublishedAt) ?
                parsedPublishedAt :
                throw new InvalidDataException($"Release {tag} is missing a valid publication time.");
        var assets = root.GetProperty("assets").Deserialize(UpdateAssetMetadataJsonSerializerContext.Default.ListUpdateAssetMetadata);
        var selectedAsset = platformHandler.SelectAsset(assets ?? [], versionString) ??
            throw new SoftwareUpdateConsistencyException($"Release {tag} has no compatible update asset.");
        ValidateAssetName(selectedAsset.Name);

        var proxyDownloadUrl =
            $"{UpdateServiceBaseUrl}/download?product=everywhere&os={Uri.EscapeDataString(platformHandler.OsIdentifier)}" +
            $"&channel={Uri.EscapeDataString(channel.ToString())}&type={Uri.EscapeDataString(platformHandler.GetDownloadType())}" +
            $"&version={Uri.EscapeDataString(tag)}";
        var directDownloadUrl = $"{GitHubDirectUrlBase}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(selectedAsset.Name)}";

        return new SoftwareUpdateMetadata(
            channel,
            tag,
            version,
            publishedAt,
            new SoftwareUpdateMetadata.UpdateAsset(
                selectedAsset.Name,
                selectedAsset.Digest,
                selectedAsset.Size,
                proxyDownloadUrl,
                directDownloadUrl));
    }

    private async Task ApplySuccessfulDiscoveryAsync(UpdateChannel channel, SoftwareUpdateMetadata? latestUpdate)
    {
        CancellationTokenSource? obsoleteRetry = null;
        if (channel != UpdateChannel)
            throw new OperationCanceledException("The selected update channel changed while checking for updates.");

        lock (_gate)
        {
            _latestUpdate = latestUpdate;
            _lastCheckTime = DateTimeOffset.UtcNow;
            if (!string.Equals(_autoDownloadRetryTag, latestUpdate?.Tag, StringComparison.Ordinal))
            {
                _autoDownloadRetryTag = latestUpdate?.Tag;
                _autoDownloadRetryCount = 0;
                obsoleteRetry = _retryCancellationTokenSource;
                _retryCancellationTokenSource = null;
            }
        }

        obsoleteRetry?.Cancel();
        obsoleteRetry?.Dispose();
        NotifyDiscoveryChanged();
        await ReconcileAutomaticPolicyAsync(latestUpdate);
    }

    private async Task ReconcileAutomaticPolicyAsync(SoftwareUpdateMetadata? latestUpdate)
    {
        ActiveDownload? downloadToCancel = null;
        ReadyPackage? packageToDelete = null;
        var recentRelease = latestUpdate is not null && DateTimeOffset.UtcNow - latestUpdate.PublishedAt < MinReleaseAgeForAutoDownload;

        lock (_gate)
        {
            var manualOperation = _manualOperation is { Task.IsCompleted: false } currentManualOperation ? currentManualOperation : null;
            var manualOwnsReadyPackage = manualOperation is not null &&
                (_readyPackage is null ||
                    manualOperation.Target is null ||
                    _readyPackage.Metadata.HasSamePackageIdentity(manualOperation.Target));
            if (_activeDownload is { Intent: SoftwareUpdateOperationIntent.Automatic } activeDownload &&
                (latestUpdate is null || recentRelease || !activeDownload.Target.HasSamePackageIdentity(latestUpdate)))
            {
                downloadToCancel = activeDownload;
                activeDownload.CancellationTokenSource.Cancel();
            }

            if (_readyPackage is { Provenance: PackageProvenance.Automatic } readyPackage &&
                !manualOwnsReadyPackage &&
                (latestUpdate is null || recentRelease || !readyPackage.Metadata.HasSamePackageIdentity(latestUpdate)))
            {
                packageToDelete = readyPackage;
                _readyPackage = null;
            }
        }

        if (packageToDelete is not null) NotifyReadyPackageChanged();

        if (downloadToCancel is not null)
        {
            await IgnoreCancellationAndFailureAsync(downloadToCancel.Task);
            TryDeleteUnownedPackageFiles(downloadToCancel.Target);
        }

        if (packageToDelete is not null)
        {
            TryDeleteUnownedPackageFiles(packageToDelete.Metadata);
            DeletePackageManifest();
        }

        if (latestUpdate is null)
        {
            CancelEligibilityTimer();
            return;
        }

        if (recentRelease)
        {
            ScheduleEligibilityCheck(latestUpdate);
            return;
        }

        CancelEligibilityTimer();
        ReadyPackage? matchingReadyPackage;
        lock (_gate)
        {
            matchingReadyPackage = _readyPackage is { } readyPackage && readyPackage.Metadata.HasSamePackageIdentity(latestUpdate) ?
                readyPackage :
                null;
        }

        if (matchingReadyPackage is not null)
        {
            if (!matchingReadyPackage.IsConfirmedLatest)
            {
                SetReadyPackage(matchingReadyPackage with { IsConfirmedLatest = true });
            }
            return;
        }

        if (!_automaticEnabled || nativeHelper.IsLowDataModeActive) return;
        StartAutomaticDownload(latestUpdate);
    }

    private Task StartOrJoinManualOperationAsync(
        SoftwareUpdateMetadata? requestedTarget,
        CancellationToken cancellationToken)
    {
        ClearAutomaticDownloadSuppression();
        Task task;

        lock (_gate)
        {
            if (_manualOperation is { Task.IsCompleted: false } currentOperation)
            {
                var joinsCurrentTarget = requestedTarget is null || currentOperation.Target?.HasSamePackageIdentity(requestedTarget) == true;
                if (!joinsCurrentTarget)
                {
                    throw new InvalidOperationException(
                        "Another update target is already being installed. Cancel it before selecting a different release.");
                }

                task = currentOperation.Task;
            }
            else
            {
                var initialTarget = requestedTarget;
                if (initialTarget is null && _currentOperation is
                    {
                        Intent: SoftwareUpdateOperationIntent.Automatic,
                        IsActive: true,
                        Target: { } automaticTarget
                    } && automaticTarget.Channel == UpdateChannel)
                {
                    initialTarget = automaticTarget;
                }

                var operation = new ManualOperation(
                    ++_nextOperationId,
                    initialTarget,
                    CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellationTokenSource.Token));
                _manualOperation = operation;
                _currentOperation = new SoftwareUpdateOperation(
                    operation.Id,
                    SoftwareUpdateOperationIntent.Manual,
                    SoftwareUpdateOperationStage.Checking,
                    initialTarget);
                operation.Task = Task.Run(() => RunManualOperationAsync(operation));
                task = operation.Task;
            }
        }

        NotifyOperationChanged();
        return task.WaitAsync(cancellationToken);
    }

    private async Task RunManualOperationAsync(ManualOperation operation)
    {
        using var activity = _activitySource.StartActivity();

        try
        {
            await EnsureRecoveryAsync(operation.CancellationTokenSource.Token);
            var target = operation.Target;
            ReadyPackage? readyPackage;
            SoftwareUpdateMetadata? latestUpdate;

            lock (_gate)
            {
                readyPackage = _readyPackage?.Metadata.Channel == UpdateChannel ? _readyPackage : null;
                latestUpdate = _latestUpdate?.Channel == UpdateChannel ? _latestUpdate : null;
            }

            if (target is null && readyPackage is not null &&
                (latestUpdate is null || readyPackage.Metadata.HasSamePackageIdentity(latestUpdate)))
            {
                target = readyPackage.Metadata;
            }

            if (target is null)
            {
                PublishManualOperation(operation, SoftwareUpdateOperationStage.Checking);
                target = await GetLatestUpdateAsync(forceFresh: true, operation.CancellationTokenSource.Token);
                if (target is null)
                {
                    PublishManualOperation(operation, SoftwareUpdateOperationStage.Completed);
                    return;
                }
            }

            operation.Target = target;
            var assetPath = await TryGetValidatedReadyPathAsync(target, operation);
            if (assetPath is null)
            {
                assetPath = await EnsureManualDownloadAsync(target, operation);
                operation.CancellationTokenSource.Token.ThrowIfCancellationRequested();
                PublishManualOperation(operation, SoftwareUpdateOperationStage.Validating, 1d);
                SetReadyPackage(
                    new ReadyPackage(
                        target,
                        assetPath,
                        PackageProvenance.Manual,
                        LatestUpdate?.HasSamePackageIdentity(target) == true));
            }

            operation.CancellationTokenSource.Token.ThrowIfCancellationRequested();
            PublishManualOperation(operation, SoftwareUpdateOperationStage.Installing, 1d);
            await platformHandler.ExecuteUpdateAsync(assetPath, CancellationToken.None);
            PublishManualOperation(operation, SoftwareUpdateOperationStage.Completed, 1d);
        }
        catch (OperationCanceledException) when (operation.CancellationTokenSource.IsCancellationRequested)
        {
            PublishManualOperation(operation, SoftwareUpdateOperationStage.Canceled);
        }
        catch (Exception ex)
        {
            var handledException = new HandledException(ex, new DynamicLocaleKey(LocaleKey.SoftwareUpdater_PerformUpdate_FailedToast_Message));
            activity?.SetStatus(ActivityStatusCode.Error, handledException.Message);
            logger.LogError(handledException, "Failed to perform update.");
            PublishManualOperation(operation, SoftwareUpdateOperationStage.Failed, error: handledException);
            throw handledException;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_manualOperation, operation)) _manualOperation = null;
            }

            operation.CancellationTokenSource.Dispose();
        }
    }

    private async Task<string?> TryGetValidatedReadyPathAsync(
        SoftwareUpdateMetadata target,
        ManualOperation operation)
    {
        ReadyPackage? package;
        lock (_gate)
        {
            package = _readyPackage is { } readyPackage && readyPackage.Metadata.HasSamePackageIdentity(target) ? readyPackage : null;
        }

        if (package is null) return null;

        PublishManualOperation(operation, SoftwareUpdateOperationStage.Validating, 1d);
        if (await ValidatePackageAsync(package.Metadata, package.Path, operation.CancellationTokenSource.Token))
            return package.Path;

        logger.LogWarning("Discarding invalid cached update package {FileName}.", package.Metadata.Asset.Name);
        ClearReadyPackage(package);
        DeletePackageFiles(package.Metadata);
        return null;
    }

    private async Task<string> EnsureManualDownloadAsync(
        SoftwareUpdateMetadata target,
        ManualOperation operation)
    {
        while (true)
        {
            operation.CancellationTokenSource.Token.ThrowIfCancellationRequested();
            ActiveDownload? downloadToWaitFor;
            var startsDownload = false;
            var joinsDownload = false;
            var deletesOldPackage = false;

            lock (_gate)
            {
                if (_activeDownload is null)
                {
                    downloadToWaitFor = StartDownloadNoLock(target, SoftwareUpdateOperationIntent.Manual, operation);
                    startsDownload = true;
                }
                else
                {
                    downloadToWaitFor = _activeDownload;
                    if (downloadToWaitFor.Intent == SoftwareUpdateOperationIntent.Manual)
                    {
                        Debug.Assert(downloadToWaitFor.OperationId == operation.Id);
                        Debug.Assert(downloadToWaitFor.Target.HasSamePackageIdentity(target));
                        joinsDownload = true;
                    }
                    else
                    {
                        deletesOldPackage = !downloadToWaitFor.Target.HasSamePackageIdentity(target);
                        downloadToWaitFor.CancellationTokenSource.Cancel();
                        _currentOperation = new SoftwareUpdateOperation(
                            operation.Id,
                            SoftwareUpdateOperationIntent.Manual,
                            SoftwareUpdateOperationStage.Cancelling,
                            target,
                            downloadToWaitFor.Progress);
                    }
                }
            }

            NotifyOperationChanged();

            if (startsDownload || joinsDownload)
            {
                // The download already shares the operation token. Keep ownership until its
                // finally block releases the writer; cancelling the wait would publish Canceled too early.
                return await downloadToWaitFor.Task;
            }

            await IgnoreCancellationAndFailureAsync(downloadToWaitFor.Task);
            if (deletesOldPackage)
                DeletePackageFiles(downloadToWaitFor.Target);
            operation.CancellationTokenSource.Token.ThrowIfCancellationRequested();
        }
    }

    private ActiveDownload StartDownloadNoLock(
        SoftwareUpdateMetadata target,
        SoftwareUpdateOperationIntent intent,
        ManualOperation? manualOperation)
    {
        var operationId = manualOperation?.Id ?? ++_nextOperationId;
        var cancellationTokenSource = manualOperation is null ?
            CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellationTokenSource.Token) :
            CancellationTokenSource.CreateLinkedTokenSource(
                _disposeCancellationTokenSource.Token,
                manualOperation.CancellationTokenSource.Token);
        var download = new ActiveDownload(operationId, target, intent, cancellationTokenSource);
        _activeDownload = download;
        _currentOperation = new SoftwareUpdateOperation(
            operationId,
            intent,
            SoftwareUpdateOperationStage.Downloading,
            target);
        download.Task = Task.Run(() => DownloadOwnedAssetAsync(download));
        return download;
    }

    private async Task<string> DownloadOwnedAssetAsync(ActiveDownload download)
    {
        try
        {
            var progress = new CallbackProgress(value => PublishDownloadProgress(download, value));
            var installPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
            var assetDownloadPath = Path.Combine(installPath, download.Target.Asset.Name);
            long? bandwidthLimit = download.Intent == SoftwareUpdateOperationIntent.Automatic ? AutoDownloadBytesPerSecond : null;

            return await fileDownloadService.DownloadAsync(
                new FileDownloadRequest(
                    assetDownloadPath,
                    [
                        new FileDownloadSource(download.Target.Asset.DirectDownloadUrl, "GitHub"),
                        new FileDownloadSource(download.Target.Asset.ProxyDownloadUrl, "Proxy")
                    ],
                    download.Target.Asset.Size,
                    download.Target.Asset.Digest,
                    bandwidthLimit),
                progress,
                download.CancellationTokenSource.Token);
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_activeDownload, download)) _activeDownload = null;
            }

            download.CancellationTokenSource.Dispose();
        }
    }

    private void PublishDownloadProgress(ActiveDownload download, double progress)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeDownload, download)) return;
            if (_currentOperation is { Id: var operationId, Stage: SoftwareUpdateOperationStage.Cancelling } &&
                operationId == download.OperationId)
                return;

            var now = DateTimeOffset.UtcNow;
            if (progress < 1d && now - download.LastProgressPublish < TimeSpan.FromMilliseconds(100)) return;
            download.LastProgressPublish = now;
            download.Progress = Math.Clamp(progress, 0d, 1d);
            if (_manualOperation is { } manualOperation && manualOperation.Id == download.OperationId)
            {
                _currentOperation = new SoftwareUpdateOperation(
                    manualOperation.Id,
                    SoftwareUpdateOperationIntent.Manual,
                    SoftwareUpdateOperationStage.Downloading,
                    download.Target,
                    download.Progress);
            }
            else if (_manualOperation is null)
            {
                _currentOperation = new SoftwareUpdateOperation(
                    download.OperationId,
                    SoftwareUpdateOperationIntent.Automatic,
                    SoftwareUpdateOperationStage.Downloading,
                    download.Target,
                    download.Progress);
            }
            else
            {
                return;
            }
        }

        NotifyOperationChanged();
    }

    private void StartAutomaticDownload(SoftwareUpdateMetadata target)
    {
        ActiveDownload download;

        lock (_gate)
        {
            if (!_automaticEnabled || _disposed || _manualOperation is { Task.IsCompleted: false }) return;
            if (_automaticDownloadWorkflow is { IsCompleted: false }) return;
            if (_activeDownload is not null) return;
            if (_latestUpdate?.HasSamePackageIdentity(target) != true) return;
            if (_readyPackage?.Metadata.HasSamePackageIdentity(target) == true) return;
            if (string.Equals(_suppressedAutomaticDownloadTag, target.Tag, StringComparison.Ordinal)) return;

            download = StartDownloadNoLock(target, SoftwareUpdateOperationIntent.Automatic, null);
            _automaticDownloadWorkflow = Task.Run(() => CompleteAutomaticDownloadAsync(download));
            _automaticDownloadWorkflow.Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }

        NotifyOperationChanged();
    }

    private async Task CompleteAutomaticDownloadAsync(ActiveDownload download)
    {
        var shouldRetry = false;
        SoftwareUpdateMetadata? discoveredUpdate = null;

        try
        {
            var assetPath = await download.Task;
            if (IsAutomaticWorkflowSuperseded()) return;
            PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Validating, 1d);

            try
            {
                discoveredUpdate = await GetLatestUpdateAsync(forceFresh: true, _disposeCancellationTokenSource.Token);
            }
            catch (OperationCanceledException) when (_disposeCancellationTokenSource.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Could not confirm the latest release after automatic download; retaining the validated package for manual installation.");
                if (IsAutomaticWorkflowSuperseded()) return;
                SetReadyPackage(
                    new ReadyPackage(
                        download.Target,
                        assetPath,
                        PackageProvenance.Automatic,
                        IsConfirmedLatest: false));
                PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Completed, 1d);
                return;
            }

            if (IsAutomaticWorkflowSuperseded()) return;
            if (discoveredUpdate?.HasSamePackageIdentity(download.Target) == true)
            {
                SetReadyPackage(
                    new ReadyPackage(
                        download.Target,
                        assetPath,
                        PackageProvenance.Automatic,
                        IsConfirmedLatest: true));
                lock (_gate) _autoDownloadRetryCount = 0;
                PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Completed, 1d);
                return;
            }

            TryDeleteUnownedPackageFiles(download.Target);
            PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Completed, 1d);
        }
        catch (OperationCanceledException)
        {
            PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Canceled);
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogWarning(ex, "Automatic update download failed for {Tag}.", download.Target.Tag);
            shouldRetry = !IsAutomaticWorkflowSuperseded();
            PublishAutomaticOperation(download, SoftwareUpdateOperationStage.Failed, error: ex);
        }
        finally
        {
            lock (_gate)
            {
                _automaticDownloadWorkflow = null;
            }
        }

        if (shouldRetry)
        {
            ScheduleAutomaticRetry(download.Target);
        }
        else if (discoveredUpdate is not null)
        {
            await ReconcileAutomaticPolicyAsync(discoveredUpdate);
        }
    }

    private bool IsAutomaticWorkflowSuperseded()
    {
        lock (_gate) return _manualOperation is { Task.IsCompleted: false };
    }

    private void ScheduleAutomaticRetry(SoftwareUpdateMetadata target)
    {
        TimeSpan delay;
        CancellationToken token;

        lock (_gate)
        {
            if (!_automaticEnabled || _latestUpdate?.HasSamePackageIdentity(target) != true) return;

            if (!string.Equals(_autoDownloadRetryTag, target.Tag, StringComparison.Ordinal))
            {
                _autoDownloadRetryTag = target.Tag;
                _autoDownloadRetryCount = 0;
            }

            _autoDownloadRetryCount++;
            if (_autoDownloadRetryCount >= MaxAutoDownloadRetries) return;

            delay = TimeSpan.FromHours(Math.Pow(2, _autoDownloadRetryCount - 1));
            _retryCancellationTokenSource?.Cancel();
            _retryCancellationTokenSource?.Dispose();
            _retryCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellationTokenSource.Token);
            token = _retryCancellationTokenSource.Token;
        }

        Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        await GetLatestUpdateAsync(forceFresh: true, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Automatic update retry check failed.");
                    }
                },
                token)
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private void ScheduleEligibilityCheck(SoftwareUpdateMetadata target)
    {
        CancellationToken token;
        var delay = target.PublishedAt + MinReleaseAgeForAutoDownload - DateTimeOffset.UtcNow;
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;

        lock (_gate)
        {
            if (!_automaticEnabled) return;

            _eligibilityCancellationTokenSource?.Cancel();
            _eligibilityCancellationTokenSource?.Dispose();
            _eligibilityCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellationTokenSource.Token);
            token = _eligibilityCancellationTokenSource.Token;
        }

        Task.Run(
                async () =>
                {
                    try
                    {
                        await Task.Delay(delay, token);
                        await GetLatestUpdateAsync(forceFresh: true, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to refresh update eligibility at the stabilization boundary.");
                    }
                },
                token)
            .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
    }

    private void CancelEligibilityTimer()
    {
        lock (_gate)
        {
            _eligibilityCancellationTokenSource?.Cancel();
            _eligibilityCancellationTokenSource?.Dispose();
            _eligibilityCancellationTokenSource = null;
        }
    }

    private void PublishManualOperation(
        ManualOperation operation,
        SoftwareUpdateOperationStage stage,
        double progress = 0d,
        Exception? error = null)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_manualOperation, operation)) return;
            _currentOperation = new SoftwareUpdateOperation(
                operation.Id,
                SoftwareUpdateOperationIntent.Manual,
                stage,
                operation.Target,
                progress,
                error);
        }

        NotifyOperationChanged();
    }

    private void PublishAutomaticOperation(
        ActiveDownload download,
        SoftwareUpdateOperationStage stage,
        double progress = 0d,
        Exception? error = null)
    {
        lock (_gate)
        {
            if (_manualOperation is { Task.IsCompleted: false }) return;
            if (_currentOperation?.Id != download.OperationId) return;

            _currentOperation = new SoftwareUpdateOperation(
                download.OperationId,
                SoftwareUpdateOperationIntent.Automatic,
                stage,
                download.Target,
                progress,
                error);
        }

        NotifyOperationChanged();
    }

    private async Task EnsureRecoveryAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_gate)
        {
            _recoveryTask ??= Task.Run(() => RecoverReadyPackageAsync(_disposeCancellationTokenSource.Token));
            task = _recoveryTask;
        }

        await task.WaitAsync(cancellationToken);
    }

    private async Task RecoverReadyPackageAsync(CancellationToken cancellationToken)
    {
        try
        {
            var manifestPath = GetPackageManifestPath();
            if (!File.Exists(manifestPath)) return;

            SoftwareUpdatePackageManifest? manifest;
            await using (var stream = File.OpenRead(manifestPath))
            {
                manifest = await JsonSerializer.DeserializeAsync(
                    stream,
                    SoftwareUpdatePackageJsonSerializerContext.Default.SoftwareUpdatePackageManifest,
                    cancellationToken);
            }

            if (manifest is null ||
                !Enum.TryParse<UpdateChannel>(manifest.Channel, ignoreCase: true, out var channel) ||
                !SemanticVersion.TryParse(manifest.Version, out var version) ||
                version.Channel != channel ||
                version <= RuntimeConstants.Version ||
                manifest.Tag.IsNullOrWhiteSpace() ||
                manifest.AssetName.IsNullOrWhiteSpace())
            {
                DeletePackageManifest();
                return;
            }

            ValidateAssetName(manifest.AssetName);
            var metadata = CreateMetadataFromManifest(manifest, channel, version);
            var path = GetPackagePath(metadata);
            if (!await ValidatePackageAsync(metadata, path, cancellationToken))
            {
                DeletePackageManifest();
                return;
            }

            SetReadyPackage(
                new ReadyPackage(
                    metadata,
                    path,
                    manifest.Provenance,
                    // A previous session's discovery cannot confirm the current latest release.
                    IsConfirmedLatest: false),
                persist: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Could not recover the cached update package.");
        }
    }

    private SoftwareUpdateMetadata CreateMetadataFromManifest(
        SoftwareUpdatePackageManifest manifest,
        UpdateChannel channel,
        SemanticVersion version)
    {
        var proxyDownloadUrl =
            $"{UpdateServiceBaseUrl}/download?product=everywhere&os={Uri.EscapeDataString(platformHandler.OsIdentifier)}" +
            $"&channel={Uri.EscapeDataString(channel.ToString())}&type={Uri.EscapeDataString(platformHandler.GetDownloadType())}" +
            $"&version={Uri.EscapeDataString(manifest.Tag)}";
        var directDownloadUrl =
            $"{GitHubDirectUrlBase}/{Uri.EscapeDataString(manifest.Tag)}/{Uri.EscapeDataString(manifest.AssetName)}";

        return new SoftwareUpdateMetadata(
            channel,
            manifest.Tag,
            version,
            manifest.PublishedAt,
            new SoftwareUpdateMetadata.UpdateAsset(
                manifest.AssetName,
                manifest.Digest,
                manifest.Size,
                proxyDownloadUrl,
                directDownloadUrl));
    }

    private void SetReadyPackage(ReadyPackage package, bool persist = true)
    {
        lock (_gate)
        {
            _readyPackage = package;
        }

        NotifyReadyPackageChanged();
        if (!persist) return;

        try
        {
            var manifest = new SoftwareUpdatePackageManifest(
                package.Metadata.Channel.ToString(),
                package.Metadata.Tag,
                package.Metadata.Version.ToString(),
                package.Metadata.PublishedAt,
                package.Metadata.Asset.Name,
                package.Metadata.Asset.Digest,
                package.Metadata.Asset.Size,
                package.Provenance,
                package.IsConfirmedLatest);
            var manifestPath = GetPackageManifestPath();
            var temporaryPath = manifestPath + ".tmp";
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                SoftwareUpdatePackageJsonSerializerContext.Default.SoftwareUpdatePackageManifest);

            lock (_manifestGate)
            {
                lock (_gate)
                {
                    if (_readyPackage != package) return;
                }

                File.WriteAllBytes(temporaryPath, manifestBytes);

                lock (_gate)
                {
                    if (_readyPackage != package)
                    {
                        TryDeleteFile(temporaryPath);
                        return;
                    }
                }

                File.Move(temporaryPath, manifestPath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Could not persist update package metadata.");
            DeletePackageManifestIfCurrent(package);
        }
    }

    private void ClearReadyPackage(ReadyPackage expectedPackage)
    {
        lock (_gate)
        {
            if (_readyPackage != expectedPackage) return;
            _readyPackage = null;
        }

        DeletePackageManifest();
        NotifyReadyPackageChanged();
    }

    private static async Task<bool> ValidatePackageAsync(
        SoftwareUpdateMetadata metadata,
        string path,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists || fileInfo.Length != metadata.Asset.Size) return false;

        var expectedDigest = NormalizeDigest(metadata.Asset.Digest);
        if (expectedDigest is null) return true;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actualDigest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
        return string.Equals(actualDigest, expectedDigest, StringComparison.OrdinalIgnoreCase);
    }

    private void CleanupOldUpdates()
    {
        try
        {
            var updatesPath = RuntimeConstants.EnsureWritableDataFolderPath("updates");
            foreach (var file in Directory.EnumerateFiles(updatesPath))
            {
                var fileName = Path.GetFileName(file);
                if (fileName is PackageManifestFileName or PackageManifestFileName + ".tmp") continue;
                if (!platformHandler.TryParseUpdatePackageVersion(fileName, out var version) || version is null) continue;
                if (version > RuntimeConstants.Version) continue;

                TryDeleteFile(file);
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "An error occurred during old updates cleanup.");
        }
    }

    private static void ValidateAssetName(string assetName)
    {
        if (Path.IsPathRooted(assetName) || !string.Equals(Path.GetFileName(assetName), assetName, StringComparison.Ordinal))
            throw new InvalidDataException("Update asset name must be a file name without a path.");
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (digest.IsNullOrWhiteSpace()) return null;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest;
    }

    private static string GetPackagePath(SoftwareUpdateMetadata metadata) =>
        Path.Combine(RuntimeConstants.EnsureWritableDataFolderPath("updates"), metadata.Asset.Name);

    private static string GetPackageManifestPath() =>
        Path.Combine(RuntimeConstants.EnsureWritableDataFolderPath("updates"), PackageManifestFileName);

    private void DeletePackageFiles(SoftwareUpdateMetadata metadata)
    {
        var path = GetPackagePath(metadata);
        TryDeleteFile(path);
        TryDeleteFile(path + ".part");
    }

    private bool TryDeleteUnownedPackageFiles(SoftwareUpdateMetadata metadata)
    {
        // Cleanup is reserved by the same state boundary as download startup. The old writer has already
        // exited, and keeping the boundary through these two deletes prevents a replacement from opening
        // the path between the ownership check and cleanup.
        lock (_gate)
        {
            if (IsPackagePathOwnedNoLock(metadata)) return false;
            DeletePackageFiles(metadata);
            return true;
        }
    }

    private bool IsPackagePathOwnedNoLock(SoftwareUpdateMetadata metadata)
    {
        var path = GetPackagePath(metadata);
        if (_activeDownload is { } activeDownload && HaveSamePackagePath(activeDownload.Target, path)) return true;
        if (_manualOperation is { Task.IsCompleted: false } manualOperation &&
            (manualOperation.Target is null || HaveSamePackagePath(manualOperation.Target, path)))
            return true;
        if (_readyPackage is { } readyPackage && PathsEqual(readyPackage.Path, path)) return true;
        return false;
    }

    private static bool HaveSamePackagePath(SoftwareUpdateMetadata metadata, string path) =>
        PathsEqual(GetPackagePath(metadata), path);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private void DeletePackageManifest()
    {
        lock (_manifestGate)
        {
            lock (_gate)
            {
                if (_readyPackage is not null) return;
            }

            TryDeleteFile(GetPackageManifestPath());
            TryDeleteFile(GetPackageManifestPath() + ".tmp");
        }
    }

    private void DeletePackageManifestIfCurrent(ReadyPackage package)
    {
        lock (_manifestGate)
        {
            lock (_gate)
            {
                if (_readyPackage != package) return;
            }

            TryDeleteFile(GetPackageManifestPath());
            TryDeleteFile(GetPackageManifestPath() + ".tmp");
        }
    }

    private void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogInformation(ex, "Failed to delete update cache file {FileName}.", Path.GetFileName(path));
        }
    }

    private void StopAutomaticWork()
    {
        ActiveDownload? automaticDownload = null;

        lock (_gate)
        {
            _automaticEnabled = false;
            _eligibilityCancellationTokenSource?.Cancel();
            _retryCancellationTokenSource?.Cancel();
            if (_activeDownload is { Intent: SoftwareUpdateOperationIntent.Automatic } download)
                automaticDownload = download;
        }

        automaticDownload?.CancellationTokenSource.Cancel();
    }

    private static async Task IgnoreCancellationAndFailureAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private void NotifyDiscoveryChanged()
    {
        NotifyPropertyChanged(nameof(LatestUpdate));
        NotifyPropertyChanged(nameof(DisplayUpdate));
        NotifyPropertyChanged(nameof(IsUpdateActionVisible));
        NotifyPropertyChanged(nameof(LastCheckTime));
    }

    private void NotifyReadyPackageChanged()
    {
        NotifyPropertyChanged(nameof(ReadyUpdate));
        NotifyPropertyChanged(nameof(IsReadyUpdateConfirmedLatest));
        NotifyPropertyChanged(nameof(DisplayUpdate));
        NotifyPropertyChanged(nameof(IsUpdateActionVisible));
    }

    private void NotifyOperationChanged()
    {
        NotifyPropertyChanged(nameof(CurrentOperation));
        NotifyPropertyChanged(nameof(IsUpdateActionVisible));
    }

    private void NotifyPropertyChanged(string propertyName)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            OnPropertyChanged(propertyName);
            return;
        }

        Dispatcher.UIThread.Post(() => OnPropertyChanged(propertyName), DispatcherPriority.Normal);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _disposeCancellationTokenSource.Cancel();
        StopAutomaticWork();
        lock (_gate)
        {
            _manualOperation?.CancellationTokenSource.Cancel();
            _activeDownload?.CancellationTokenSource.Cancel();
#if !DEBUG
            _automaticLoopCancellationTokenSource?.Cancel();
#endif
        }

#if !DEBUG
        _automaticLoopCancellationTokenSource?.Dispose();
#endif
        _eligibilityCancellationTokenSource?.Dispose();
        _retryCancellationTokenSource?.Dispose();
        _disposeCancellationTokenSource.Dispose();
        _activitySource.Dispose();
    }

    internal enum PackageProvenance
    {
        Automatic,
        Manual
    }

    private sealed record ReadyPackage(
        SoftwareUpdateMetadata Metadata,
        string Path,
        PackageProvenance Provenance,
        bool IsConfirmedLatest
    );

    private sealed class ManualOperation(
        long id,
        SoftwareUpdateMetadata? target,
        CancellationTokenSource cancellationTokenSource
    )
    {
        public long Id { get; } = id;

        public SoftwareUpdateMetadata? Target { get; set; } = target;

        public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;

        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class ActiveDownload(
        long operationId,
        SoftwareUpdateMetadata target,
        SoftwareUpdateOperationIntent intent,
        CancellationTokenSource cancellationTokenSource
    )
    {
        public long OperationId { get; } = operationId;

        public SoftwareUpdateMetadata Target { get; } = target;

        public SoftwareUpdateOperationIntent Intent { get; } = intent;

        public CancellationTokenSource CancellationTokenSource { get; } = cancellationTokenSource;

        public Task<string> Task { get; set; } = System.Threading.Tasks.Task.FromResult(string.Empty);

        public double Progress { get; set; }

        public DateTimeOffset LastProgressPublish { get; set; }
    }

    private sealed class CallbackProgress(Action<double> callback) : IProgress<double>
    {
        public void Report(double value) => callback(value);
    }
}

internal sealed record SoftwareUpdatePackageManifest(
    string Channel,
    string Tag,
    string Version,
    DateTimeOffset PublishedAt,
    string AssetName,
    string Digest,
    long Size,
    SoftwareUpdater.PackageProvenance Provenance,
    bool IsConfirmedLatest
);

[JsonSerializable(typeof(SoftwareUpdatePackageManifest))]
internal sealed partial class SoftwareUpdatePackageJsonSerializerContext : JsonSerializerContext;
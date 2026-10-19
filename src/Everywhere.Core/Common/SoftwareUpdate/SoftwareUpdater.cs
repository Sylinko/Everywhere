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
    private const int MaxAutomaticDownloadFailures = 3;

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
                return _readyPackage is { IsConfirmedLatest: true } readyPackage && readyPackage.Metadata.Channel == UpdateChannel &&
                    readyPackage.Metadata.HasSamePackageIdentity(_latestUpdate);
            }
        }
    }

    public SoftwareUpdateMetadata? DisplayUpdate
    {
        get
        {
            lock (_gate)
            {
                if (_currentOperation is { IsActive: true, Target: { } activeTarget }) return activeTarget;
                if (_latestUpdate?.Channel == UpdateChannel) return _latestUpdate;
                if (_readyPackage?.Metadata.Channel == UpdateChannel) return _readyPackage.Metadata;
                return _currentOperation is
                        { Stage: SoftwareUpdateOperationStage.Failed or SoftwareUpdateOperationStage.Canceled, Target: { } target } &&
                    target.Channel == UpdateChannel ?
                        target :
                        null;
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

    // The gate protects ownership and presentation snapshots, never an awaited operation.
    // Recovery finishes before the sole operation may write packages or their manifest.
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private readonly SemaphoreSlim _automaticWake = new(0, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _suppressedAutomaticTags = [];
    private DateTimeOffset? _lastCheckTime;
    private SoftwareUpdateMetadata? _latestUpdate;
    private ReadyPackage? _readyPackage;
    private SoftwareUpdateOperation? _currentOperation;
    private UpdateOperation? _operation;
    private Task? _operationTask;
    private Task? _recoveryTask;
    private CancellationTokenSource? _automaticLoopSource;
    private string? _automaticFailureTag;
    private int _automaticFailureCount;
    private DateTimeOffset _automaticRetryAt;
    private bool _isDisposed;

    /// <inheritdoc />
    public void RunAutomaticCheckInBackground(TimeSpan interval, CancellationToken cancellationToken = default)
    {
#if !DEBUG
        lock (_gate)
        {
            if (_isDisposed || _automaticLoopSource is { IsCancellationRequested: false }) return;
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _automaticLoopSource = source;
            // ReSharper disable once MethodSupportsCancellation
#pragma warning disable CA2016
            Task.Run(() => RunAutomaticLoopAsync(interval, source)).Detach(IExceptionHandler.DangerouslyIgnoreAllException);
#pragma warning restore CA2016
        }
#endif
    }

    /// <inheritdoc />
    public Task RecoverCachedPackageAsync(CancellationToken cancellationToken = default)
    {
        return EnsureRecoveryAsync().WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task CheckForUpdatesAsync(bool throwOnError, CancellationToken cancellationToken = default)
    {
        // A window owns only its wait. The request snapshots the channel before leaving the caller.
        var channel = UpdateChannel;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            var token = _lifetime.Token;
            // ReSharper disable once MethodSupportsCancellation
#pragma warning disable CA2016
            return Task.Run(async () =>
#pragma warning restore CA2016
            {
                try
                {
                    await EnsureRecoveryAsync();
                    await CheckLatestAsync(channel, token);
                    WakeAutomaticLoop();
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to check for updates.");
                    if (throwOnError) throw;
                }
            }).WaitAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public Task<SoftwareUpdateMetadata> ResolveUpdateAsync(
        UpdateChannel channel,
        string fullTag,
        CancellationToken cancellationToken = default)
    {
        return FetchReleaseAsync(channel, fullTag, cancellationToken);
    }

    /// <inheritdoc />
    public Task PerformUpdateAsync(CancellationToken cancellationToken = default)
    {
        return StartManualOperation(null).WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task PerformUpdateAsync(SoftwareUpdateMetadata target, CancellationToken cancellationToken = default)
    {
        return StartManualOperation(target).WaitAsync(cancellationToken);
    }

    private Task StartManualOperation(SoftwareUpdateMetadata? target)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_operation is { } operation)
            {
                if (!operation.ShouldInstall || target is not null && operation.Target?.HasSamePackageIdentity(target) != true)
                {
                    throw new InvalidOperationException("An update is already running. Cancel it before starting another operation.");
                }
                return _operationTask.NotNull();
            }

            var channel = target?.Channel ?? UpdateChannel;
            target ??= DisplayUpdate?.Channel == channel ? DisplayUpdate : null;
            return StartOperation(channel, target, shouldInstall: true);
        }
    }

    // Called under _gate. Task.Run must not receive a scheduling token: even pre-cancelled work
    // must enter its finally block to release ownership and dispose its token source.
    private Task StartOperation(UpdateChannel channel, SoftwareUpdateMetadata? target, bool shouldInstall)
    {
        var operation = new UpdateOperation(
            channel,
            target,
            shouldInstall,
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _operation = operation;
        _currentOperation = new SoftwareUpdateOperation(SoftwareUpdateOperationStage.Checking, target);
        _operationTask = Task.Run(() => RunOperationAsync(operation));
        return _operationTask;
    }

    /// <inheritdoc />
    public void CancelCurrentOperation()
    {
        lock (_gate)
        {
            if (_operation is not { } operation || _currentOperation is not { CanCancel: true } current) return;
            if (operation.Target is { } target) _suppressedAutomaticTags.Add(target.Tag);
            _currentOperation = current with { Stage = SoftwareUpdateOperationStage.Cancelling };
            operation.Cancellation.Cancel();
        }
        NotifyOperationChanged();
    }

    private async Task RunOperationAsync(UpdateOperation operation)
    {
        var token = operation.Cancellation.Token;
        var finalStage = SoftwareUpdateOperationStage.Completed;
        var error = (Exception?)null;
        NotifyOperationChanged();
        NotifyPropertyChanged(nameof(DisplayUpdate));
        try
        {
            // Do not release the file owner while app-owned recovery is still reading the cache.
            await EnsureRecoveryAsync();
            token.ThrowIfCancellationRequested();
            var target = operation.Target;
            if (target is null)
            {
                lock (_gate)
                {
                    target = _latestUpdate?.Channel == operation.Channel ? _latestUpdate :
                        _readyPackage?.Metadata.Channel == operation.Channel ? _readyPackage.Metadata : null;
                }
                target ??= await CheckLatestAsync(operation.Channel, token);
                if (target is null) return;
                lock (_gate) operation.Target = target;
            }

            lock (_gate)
            {
                if (operation.ShouldInstall && !token.IsCancellationRequested) _suppressedAutomaticTags.Remove(target.Tag);
            }
            token.ThrowIfCancellationRequested();
            var package = GetReadyPackage(target);
            if (package is not null)
            {
                PublishOperation(operation, SoftwareUpdateOperationStage.Validating, 1d);
                if (!await ValidatePackageAsync(target, package.Path, token))
                {
                    ClearReadyPackage();
                    DeletePackageFiles(target);
                    package = null;
                }
            }

            if (package is null)
            {
                // Only this owner can discard, download, validate, or publish a package.
                if (_readyPackage is { } obsoletePackage)
                {
                    ClearReadyPackage();
                    DeletePackageFiles(obsoletePackage.Metadata);
                }
                PublishOperation(operation, SoftwareUpdateOperationStage.Downloading);
                var bandwidthLimit = operation.ShouldInstall ? (long?)null : AutoDownloadBytesPerSecond;
                var path = await fileDownloadService.DownloadAsync(
                    new FileDownloadRequest(
                        GetPackagePath(target),
                        [
                            new FileDownloadSource(target.Asset.DirectDownloadUrl, "GitHub"),
                            new FileDownloadSource(target.Asset.ProxyDownloadUrl, "Proxy")
                        ],
                        target.Asset.Size,
                        target.Asset.Digest,
                        bandwidthLimit),
                    new CallbackProgress(value => PublishProgress(operation, value)),
                    token);
                token.ThrowIfCancellationRequested();
                PublishOperation(operation, SoftwareUpdateOperationStage.Validating, 1d);
                // The downloader validates size and digest before returning this path.
                package = new ReadyPackage(
                    target,
                    path,
                    operation.ShouldInstall ? PackageProvenance.Manual : PackageProvenance.Automatic,
                    false);
            }

            if (!operation.ShouldInstall)
            {
                try
                {
                    // Fresh discovery uses the operation's channel, not the current setting.
                    var latest = await CheckLatestAsync(operation.Channel, token);
                    if (latest?.HasSamePackageIdentity(target) != true)
                    {
                        ClearReadyPackage();
                        DeletePackageFiles(target);
                        return;
                    }
                    package = package with { IsConfirmedLatest = true };
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not confirm the latest release; retaining the validated package for manual installation.");
                }
            }

            token.ThrowIfCancellationRequested();
            SetReadyPackage(package);
            if (operation.ShouldInstall)
            {
                // Cancellation and entry into the non-cancellable platform handoff are atomic.
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    _currentOperation = new SoftwareUpdateOperation(SoftwareUpdateOperationStage.Installing, target, 1d);
                }
                NotifyOperationChanged();
                await platformHandler.ExecuteUpdateAsync(package.Path, CancellationToken.None);
            }
            else
            {
                lock (_gate) _automaticFailureCount = 0;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            finalStage = SoftwareUpdateOperationStage.Canceled;
        }
        catch (Exception ex)
        {
            finalStage = SoftwareUpdateOperationStage.Failed;
            error = operation.ShouldInstall ?
                new HandledException(ex, new DynamicLocaleKey(LocaleKey.SoftwareUpdater_PerformUpdate_FailedToast_Message)) :
                HandledSystemException.Handle(ex);
            logger.LogWarning(error, "Update operation failed for {Tag}.", operation.Target?.Tag);
            if (operation.ShouldInstall) throw error;
            lock (_gate)
            {
                if (_automaticFailureTag != operation.Target?.Tag) _automaticFailureCount = 0;
                _automaticFailureTag = operation.Target?.Tag;
                _automaticFailureCount++;
                _automaticRetryAt = DateTimeOffset.UtcNow + TimeSpan.FromHours(_automaticFailureCount);
            }
        }
        finally
        {
            // No waiter cancellation can skip the downloader's cleanup or release this owner early.
            lock (_gate)
            {
                _currentOperation = new SoftwareUpdateOperation(
                    finalStage,
                    operation.Target,
                    finalStage == SoftwareUpdateOperationStage.Completed ? 1d : 0d,
                    error);
                _operation = null;
                operation.Cancellation.Dispose();
            }
            NotifyOperationChanged();
            NotifyPropertyChanged(nameof(DisplayUpdate));
            WakeAutomaticLoop();
        }
    }

    private ReadyPackage? GetReadyPackage(SoftwareUpdateMetadata target)
    {
        lock (_gate)
        {
            return _readyPackage?.Metadata.HasSamePackageIdentity(target) == true ? _readyPackage : null;
        }
    }

    private void PublishOperation(UpdateOperation operation, SoftwareUpdateOperationStage stage, double progress = 0d)
    {
        lock (_gate)
        {
            if (_operation != operation || operation.Cancellation.IsCancellationRequested) return;
            _currentOperation = new SoftwareUpdateOperation(stage, operation.Target, progress);
        }
        NotifyOperationChanged();
    }

    private void PublishProgress(UpdateOperation operation, double progress)
    {
        lock (_gate)
        {
            if (_operation != operation || _currentOperation?.Stage != SoftwareUpdateOperationStage.Downloading) return;
            var now = DateTimeOffset.UtcNow;
            if (progress < 1d && now - operation.LastProgressPublish < TimeSpan.FromMilliseconds(100)) return;
            operation.LastProgressPublish = now;
            _currentOperation = _currentOperation with { Progress = double.IsFinite(progress) ? Math.Clamp(progress, 0d, 1d) : 0d };
        }
        NotifyOperationChanged();
    }

    private async Task<SoftwareUpdateMetadata?> CheckLatestAsync(UpdateChannel channel, CancellationToken token)
    {
        // Serialize discovery so a pre-download response cannot overwrite post-download confirmation.
        // Unlike package work, these read-only checks do not acquire the operation owner.
        await _discoveryGate.WaitAsync(token);
        try
        {
            var metadata = await FetchReleaseAsync(channel, null, token);
            token.ThrowIfCancellationRequested();
            var latest = metadata.Version > RuntimeConstants.Version ? metadata : null;
            lock (_gate)
            {
                // An old operation may still confirm its snapshot after the setting changes.
                // Its result must not replace discovery already made for the newly selected channel.
                if (channel == UpdateChannel) _latestUpdate = latest;
                _lastCheckTime = DateTimeOffset.UtcNow;
                if (_readyPackage is { } package && package.Metadata.Channel == channel)
                {
                    _readyPackage = package with { IsConfirmedLatest = package.Metadata.HasSamePackageIdentity(latest) };
                }
            }
            NotifyDiscoveryChanged();
            NotifyReadyPackageChanged();
            return latest;
        }
        finally
        {
            _discoveryGate.Release();
        }
    }

    private async Task RunAutomaticLoopAsync(TimeSpan interval, CancellationTokenSource source)
    {
        var token = source.Token;
        await using var registration = token.Register(() =>
        {
            lock (_gate)
            {
                // ReSharper disable once AccessToDisposedClosure
                if (_automaticLoopSource == source && _operation is { ShouldInstall: false } operation)
                {
                    operation.Cancellation.Cancel();
                }
            }
        });
        try
        {
            await EnsureRecoveryAsync();
            token.ThrowIfCancellationRequested();
            CleanupOldUpdates();
            var nextCheck = DateTimeOffset.MinValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (DateTimeOffset.UtcNow >= nextCheck)
                {
                    try { await CheckLatestAsync(UpdateChannel, token); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex) { logger.LogWarning(ex, "Automatic update check failed."); }
                    nextCheck = DateTimeOffset.UtcNow + interval;
                }

                var wakeAt = nextCheck;
                lock (_gate)
                {
                    token.ThrowIfCancellationRequested();
                    var target = _latestUpdate;
                    if (_operation is null && target is not null && target.Channel == UpdateChannel &&
                        _readyPackage?.Metadata.HasSamePackageIdentity(target) != true &&
                        !_suppressedAutomaticTags.Contains(target.Tag) && !nativeHelper.IsLowDataModeActive)
                    {
                        if (_automaticFailureTag != target.Tag)
                        {
                            _automaticFailureTag = target.Tag;
                            _automaticFailureCount = 0;
                            _automaticRetryAt = DateTimeOffset.MinValue;
                        }
                        if (_automaticFailureCount < MaxAutomaticDownloadFailures)
                        {
                            var eligibleAt = target.PublishedAt + MinReleaseAgeForAutoDownload;
                            if (_automaticRetryAt > eligibleAt) eligibleAt = _automaticRetryAt;
                            if (eligibleAt <= DateTimeOffset.UtcNow)
                            {
                                StartOperation(target.Channel, target, shouldInstall: false)
                                    .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
                            }
                            else if (eligibleAt < wakeAt) wakeAt = eligibleAt;
                        }
                    }
                }

                var delay = wakeAt - DateTimeOffset.UtcNow;
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                if (!await _automaticWake.WaitAsync(delay, token))
                {
                    // Age and retry deadlines get a fresh check, just like the four-hour deadline.
                    nextCheck = DateTimeOffset.MinValue;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Automatic update checking stopped unexpectedly."); }
        finally
        {
            lock (_gate)
            {
                if (_automaticLoopSource == source) _automaticLoopSource = null;
                source.Dispose();
            }
        }
    }

    private void WakeAutomaticLoop()
    {
        lock (_gate)
        {
            if (_automaticWake.CurrentCount == 0) _automaticWake.Release();
        }
    }

    private Task EnsureRecoveryAsync()
    {
        lock (_gate)
        {
            var token = _lifetime.Token;
            // ReSharper disable once MethodSupportsCancellation
            return _recoveryTask ??= Task.Run(() => RecoverReadyPackageAsync(token));
        }
    }

    private async Task<SoftwareUpdateMetadata> FetchReleaseAsync(UpdateChannel channel, string? fullTag, CancellationToken cancellationToken)
    {
        using var httpClient = httpClientFactory.CreateClient(Options.DefaultName);
        var requestUrl = $"{UpdateServiceBaseUrl}/api?product=everywhere&channel={Uri.EscapeDataString(channel.ToString())}";
        if (fullTag is not null)
        {
            requestUrl += $"&version={Uri.EscapeDataString(fullTag)}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        if (fullTag is null)
        {
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        }

        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (fullTag is not null && response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new SoftwareUpdateConsistencyException($"Release detail for {fullTag} was not found.");
        }

        response.EnsureSuccessStatusCode();

        await using var jsonStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var jsonDocument = await JsonDocument.ParseAsync(jsonStream, cancellationToken: cancellationToken);
        var root = jsonDocument.RootElement;
        var tag = root.GetProperty("tag_name").GetString();
        if (tag.IsNullOrWhiteSpace()) throw new InvalidDataException("Release detail is missing tag_name.");
        if (fullTag is not null && !string.Equals(tag, fullTag, StringComparison.Ordinal))
        {
            throw new SoftwareUpdateConsistencyException($"Release detail returned {tag} instead of requested tag {fullTag}.");
        }

        var versionString = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (!SemanticVersion.TryParse(versionString, out var version))
        {
            throw new InvalidDataException($"Could not parse version from release tag {tag}.");
        }
        if (version.Channel != channel)
        {
            throw new SoftwareUpdateConsistencyException($"Release {tag} belongs to {version.Channel}, not requested channel {channel}.");
        }

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

    private SoftwareUpdateMetadata CreateMetadataFromManifest(SoftwareUpdatePackageManifest manifest, UpdateChannel channel, SemanticVersion version)
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
        lock (_gate) _readyPackage = package;
        if (persist)
        {
            try
            {
                var metadata = package.Metadata;
                var manifest = new SoftwareUpdatePackageManifest(
                    metadata.Channel.ToString(),
                    metadata.Tag,
                    metadata.Version.ToString(),
                    metadata.PublishedAt,
                    metadata.Asset.Name,
                    metadata.Asset.Digest,
                    metadata.Asset.Size,
                    package.Provenance,
                    package.IsConfirmedLatest);
                var path = GetPackageManifestPath();
                File.WriteAllBytes(
                    path + ".tmp",
                    JsonSerializer.SerializeToUtf8Bytes(
                        manifest,
                        SoftwareUpdatePackageJsonSerializerContext.Default.SoftwareUpdatePackageManifest));
                File.Move(path + ".tmp", path, overwrite: true);
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex, "Could not persist update package metadata.");
                DeletePackageManifest();
            }
        }
        NotifyReadyPackageChanged();
    }

    private void ClearReadyPackage()
    {
        lock (_gate) _readyPackage = null;
        DeletePackageManifest();
        NotifyReadyPackageChanged();
    }

    private void DeletePackageManifest()
    {
        TryDeleteFile(GetPackageManifestPath());
        TryDeleteFile(GetPackageManifestPath() + ".tmp");
    }

    private void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) { logger.LogInformation(ex, "Failed to delete update cache file {FileName}.", Path.GetFileName(path)); }
    }

    private static async Task<bool> ValidatePackageAsync(SoftwareUpdateMetadata metadata, string path, CancellationToken cancellationToken)
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
        {
            throw new InvalidDataException("Update asset name must be a file name without a path.");
        }
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (digest.IsNullOrWhiteSpace()) return null;
        return digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : digest;
    }

    private static string GetPackagePath(SoftwareUpdateMetadata metadata)
    {
        return Path.Combine(RuntimeConstants.EnsureWritableDataFolderPath("updates"), metadata.Asset.Name);
    }

    private static string GetPackageManifestPath()
    {
        return Path.Combine(RuntimeConstants.EnsureWritableDataFolderPath("updates"), PackageManifestFileName);
    }

    private void DeletePackageFiles(SoftwareUpdateMetadata metadata)
    {
        var path = GetPackagePath(metadata);
        TryDeleteFile(path);
        TryDeleteFile(path + ".part");
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


    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _lifetime.Cancel();
            _operation?.Cancellation.Cancel();
            // The in-flight tasks still use the lifetime token and semaphores. They own their
            // cleanup; disposing these managed objects here would race their continuations.
        }
    }

    internal enum PackageProvenance
    {
        Automatic,
        Manual
    }

    private sealed record ReadyPackage(SoftwareUpdateMetadata Metadata, string Path, PackageProvenance Provenance, bool IsConfirmedLatest);

    private sealed class UpdateOperation(
        UpdateChannel channel,
        SoftwareUpdateMetadata? target,
        bool shouldInstall,
        CancellationTokenSource cancellation
    )
    {
        public UpdateChannel Channel { get; } = channel;
        public SoftwareUpdateMetadata? Target { get; set; } = target;
        public bool ShouldInstall { get; } = shouldInstall;
        public CancellationTokenSource Cancellation { get; } = cancellation;
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
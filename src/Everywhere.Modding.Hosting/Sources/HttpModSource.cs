using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Fetches signed or SHA-256-verified static v1 indexes and artifacts over HTTP(S) with Happy-Eyeballs staggered mirror
/// racing, persistent ETag conditional requests, concurrent refresh coalescing, and streaming temporary file verification.
/// </summary>
public sealed class HttpModSource : IModRepositorySource
{
    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public bool IsSignatureRequired { get; }

    /// <inheritdoc />
    public string? TrustedPublicKey { get; }

    private readonly HttpClient _httpClient;
    private readonly HttpModSourceOptions _options;
    private readonly Lock _gate = new();

    private ModIndex? _cachedIndex;
    private string? _cachedETag;
    private Uri _activeIndexUri;
    private DateTimeOffset _cachedAt;
    private string? _effectivePublicKey;
    private Uri[] _discoveredMirrors = [];
    private Task<ModIndex>? _inFlightFetch;

    /// <summary>
    /// Initializes an HTTP(S) static repository source using a caller-owned <see cref="HttpClient"/>.
    /// </summary>
    public HttpModSource(HttpClient httpClient, HttpModSourceOptions options)
    {
        if (!ModValidationRules.IsValidIdentifier(options.Id))
        {
            throw new ModValidationException("InvalidSourceId", $"Invalid source identifier '{options.Id}'.");
        }

        ModValidationRules.ValidateHttpUri(options.PrimaryIndexUri.AbsoluteUri);
        foreach (var mirror in options.MirrorIndexUris)
        {
            ModValidationRules.ValidateHttpUri(mirror.AbsoluteUri);
        }

        if (options.TrustedPublicKey is not null)
        {
            ModSignature.ValidatePublicKey(options.TrustedPublicKey);
        }

        _httpClient = httpClient;
        _options = options;
        Id = options.Id;
        IsSignatureRequired = options.IsSignatureRequired;
        TrustedPublicKey = options.TrustedPublicKey;
        _effectivePublicKey = options.TrustedPublicKey;
        _activeIndexUri = options.PrimaryIndexUri;
    }

    /// <inheritdoc />
    public async Task<ModIndex> FetchIndexAsync(bool shouldForceRefresh = false, CancellationToken cancellationToken = default)
    {
        Task<ModIndex> task;
        lock (_gate)
        {
            if (!shouldForceRefresh &&
                _cachedIndex is not null &&
                DateTimeOffset.UtcNow - _cachedAt < _options.CacheTtl)
            {
                return _cachedIndex;
            }

            if (shouldForceRefresh || _inFlightFetch is null)
            {
                var fetchTask = FetchIndexCoreAsync(cancellationToken);
                _inFlightFetch = fetchTask;
                task = fetchTask;
            }
            else
            {
                task = _inFlightFetch;
            }
        }

        return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<Stream> OpenArtifactAsync(
        string modId,
        ModIndexVersion version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        var effectiveKey = await EnsureEffectivePublicKeyAsync(cancellationToken).ConfigureAwait(false);
        var candidateUris = BuildArtifactUris(version.Artifact.Url);
        var downloadRoot = _options.CacheDirectory is not null
            ? Path.Combine(_options.CacheDirectory, "downloads")
            : Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Downloads");
        Directory.CreateDirectory(downloadRoot);

        Exception? lastError = null;
        foreach (var uri in candidateUris)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tempPath = Path.Combine(downloadRoot, Guid.NewGuid().ToString("N") + ".zip.tmp");
            var shouldKeepFile = false;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                await using (var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var fileStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await ModArtifactVerifier.VerifyStreamAsync(
                            responseStream,
                            modId,
                            version,
                            IsSignatureRequired,
                            effectiveKey,
                            fileStream,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var resultStream = new FileStream(
                    tempPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    81920,
                    FileOptions.DeleteOnClose | FileOptions.Asynchronous);
                shouldKeepFile = true;
                return resultStream;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
            finally
            {
                if (!shouldKeepFile)
                {
                    TryDeleteFile(tempPath);
                }
            }
        }

        throw lastError ?? new ModValidationException("DownloadFailed", $"Failed to download artifact '{modId}@{version.Version}'.");
    }

    /// <inheritdoc />
    public async Task<string?> ReadTextDocumentAsync(string documentReference, CancellationToken cancellationToken = default)
    {
        var candidateUris = BuildArtifactUris(documentReference);
        Exception? lastError = null;

        foreach (var uri in candidateUris)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await ModArtifactVerifier.ReadBoundedUtf8Async(
                        stream,
                        _options.MaxTextDocumentBytes,
                        documentReference,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastError = exception;
            }
        }

        if (lastError is not null)
        {
            throw lastError;
        }

        return null;
    }

    private async Task<ModIndex> FetchIndexCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            var diskCache = TryLoadDiskCache();
            var endpoints = BuildIndexEndpoints();

            var winner = endpoints.Length == 1
                ? await FetchSingleEndpointAsync(endpoints[0], diskCache, cancellationToken).ConfigureAwait(false)
                : await RaceIndexEndpointsAsync(endpoints, diskCache, cancellationToken).ConfigureAwait(false);

            var mirrors = ParseMirrorUris(winner.Index.Mirrors);
            lock (_gate)
            {
                _cachedIndex = winner.Index;
                _cachedETag = winner.ETag;
                _activeIndexUri = winner.EndpointUri;
                _cachedAt = DateTimeOffset.UtcNow;
                _effectivePublicKey = TrustedPublicKey ?? winner.Index.PublicKey;
                _discoveredMirrors = mirrors;
            }

            if (winner.RawBytes is not null)
            {
                SaveDiskCache(winner.RawBytes, winner.EndpointUri, winner.ETag);
            }
            else if (diskCache is not null)
            {
                SaveDiskCacheMetadata(winner.EndpointUri, winner.ETag);
            }

            return winner.Index;
        }
        finally
        {
            lock (_gate)
            {
                _inFlightFetch = null;
            }
        }
    }

    private async Task<FetchedIndexResult> RaceIndexEndpointsAsync(
        Uri[] endpoints,
        CachedIndexState? diskCache,
        CancellationToken cancellationToken
    )
    {
        using var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var winnerTcs = new TaskCompletionSource<FetchedIndexResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var cancellationRegistration = cancellationToken.Register(() => winnerTcs.TrySetCanceled(cancellationToken));

        var coordinator = new EndpointRaceCoordinator(endpoints.Length, diskCache, raceCts, winnerTcs, cancellationToken);
        var allTasks = new Task[endpoints.Length];

        for (var i = 0; i < endpoints.Length; i++)
        {
            var endpointUri = endpoints[i];
            var stagger = TimeSpan.FromTicks(_options.StaggerDelay.Ticks * i);
            allTasks[i] = RunEndpointAttemptAsync(endpointUri, stagger, coordinator);
        }

        try
        {
            return await winnerTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            await raceCts.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(allTasks).ConfigureAwait(false);
        }
    }

    private async Task RunEndpointAttemptAsync(Uri endpointUri, TimeSpan stagger, EndpointRaceCoordinator coordinator)
    {
        try
        {
            if (stagger > TimeSpan.Zero)
            {
                await Task.Delay(stagger, coordinator.RaceCancellation.Token).ConfigureAwait(false);
            }

            var result = await FetchSingleEndpointAsync(
                    endpointUri,
                    coordinator.DiskCache,
                    coordinator.RaceCancellation.Token)
                .ConfigureAwait(false);
            if (coordinator.Winner.TrySetResult(result))
            {
                await coordinator.RaceCancellation.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            coordinator.RaceCancellation.IsCancellationRequested &&
            !coordinator.ExternalCancellation.IsCancellationRequested)
        {
            // Lost the Happy-Eyeballs race after another endpoint succeeded.
        }
        catch (Exception exception)
        {
            coordinator.Errors.Enqueue(exception);
        }
        finally
        {
            if (coordinator.DecrementRemaining() == 0 && !coordinator.Winner.Task.IsCompleted)
            {
                if (coordinator.ExternalCancellation.IsCancellationRequested)
                {
                    coordinator.Winner.TrySetCanceled(coordinator.ExternalCancellation);
                }
                else
                {
                    coordinator.Winner.TrySetException(SelectBestError(coordinator.Errors));
                }
            }
        }
    }

    private async Task<FetchedIndexResult> FetchSingleEndpointAsync(
        Uri endpointUri,
        CachedIndexState? diskCache,
        CancellationToken cancellationToken
    )
    {
        CachedIndexState? reusableCache = null;
        lock (_gate)
        {
            if (_cachedIndex is not null && _cachedETag is not null && UriEquals(_activeIndexUri, endpointUri))
            {
                reusableCache = new CachedIndexState(_cachedIndex, _cachedETag, _activeIndexUri);
            }
        }

        if (reusableCache is null && diskCache is not null && UriEquals(diskCache.EndpointUri, endpointUri))
        {
            reusableCache = diskCache;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpointUri);
        if (reusableCache is { ETag: { } etag } && EntityTagHeaderValue.TryParse(etag, out var parsedETag))
        {
            request.Headers.IfNoneMatch.Add(parsedETag);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified && reusableCache is not null)
        {
            ModArtifactVerifier.ValidateIndexTrust(reusableCache.Index, Id, IsSignatureRequired, TrustedPublicKey);
            var refreshedETag = response.Headers.ETag?.Tag ?? reusableCache.ETag;
            return new FetchedIndexResult(reusableCache.Index, endpointUri, refreshedETag, null);
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var rawBytes = await ModArtifactVerifier.ReadBoundedBytesAsync(
                stream,
                _options.MaxIndexBytes,
                endpointUri.AbsoluteUri,
                cancellationToken)
            .ConfigureAwait(false);

        using var parseStream = new MemoryStream(rawBytes, writable: false);
        var index = await ModIndexFile.ReadAsync(parseStream, endpointUri, cancellationToken).ConfigureAwait(false);
        ModArtifactVerifier.ValidateIndexTrust(index, Id, IsSignatureRequired, TrustedPublicKey);

        var responseETag = response.Headers.ETag?.Tag;
        return new FetchedIndexResult(index, endpointUri, responseETag, rawBytes);
    }

    private async Task<string?> EnsureEffectivePublicKeyAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedIndex is not null || !IsSignatureRequired || TrustedPublicKey is not null)
            {
                return _effectivePublicKey;
            }
        }

        await FetchIndexAsync(shouldForceRefresh: false, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _effectivePublicKey;
        }
    }

    private Uri[] BuildIndexEndpoints()
    {
        var list = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddUri(Uri uri)
        {
            if (seen.Add(uri.AbsoluteUri))
            {
                list.Add(uri);
            }
        }

        lock (_gate)
        {
            AddUri(_options.PrimaryIndexUri);
            foreach (var mirror in _options.MirrorIndexUris) AddUri(mirror);
            foreach (var discovered in _discoveredMirrors) AddUri(discovered);
        }

        return [.. list];
    }

    private Uri[] BuildArtifactUris(string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var absolute))
        {
            ModValidationRules.ValidateHttpUri(absolute.AbsoluteUri);
            return [absolute];
        }

        var endpoints = BuildOrderedFallbackBaseUris();
        var uris = new List<Uri>(endpoints.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var indexUri in endpoints)
        {
            var resolved = ModIndexFile.ResolveAssetUri(indexUri, reference);
            if (seen.Add(resolved.AbsoluteUri))
            {
                uris.Add(resolved);
            }
        }

        return [.. uris];
    }

    private Uri[] BuildOrderedFallbackBaseUris()
    {
        var list = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void AddUri(Uri uri)
        {
            if (seen.Add(uri.AbsoluteUri))
            {
                list.Add(uri);
            }
        }

        lock (_gate)
        {
            AddUri(_activeIndexUri);
            AddUri(_options.PrimaryIndexUri);
            foreach (var mirror in _options.MirrorIndexUris) AddUri(mirror);
            foreach (var discovered in _discoveredMirrors) AddUri(discovered);
        }

        return [.. list];
    }

    private CachedIndexState? TryLoadDiskCache()
    {
        if (_options.CacheDirectory is null) return null;

        var indexPath = Path.Combine(_options.CacheDirectory, $"{Id}.index.json");
        var metadataPath = Path.Combine(_options.CacheDirectory, $"{Id}.cache.json");
        if (!File.Exists(indexPath) || !File.Exists(metadataPath)) return null;

        try
        {
            using var metadataStream = File.OpenRead(metadataPath);
            var metadata = JsonSerializer.Deserialize(metadataStream, HostingJsonSerializerContext.DefaultOptions.HttpModIndexCacheMetadata);
            if (metadata is null ||
                string.IsNullOrWhiteSpace(metadata.ETag) ||
                !Uri.TryCreate(metadata.EndpointUri, UriKind.Absolute, out var endpointUri))
            {
                return null;
            }

            ModValidationRules.ValidateHttpUri(endpointUri.AbsoluteUri);
            var index = ModIndexFile.Read(indexPath, endpointUri);
            ModArtifactVerifier.ValidateIndexTrust(index, Id, IsSignatureRequired, TrustedPublicKey);
            return new CachedIndexState(index, metadata.ETag, endpointUri);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ModValidationException)
        {
            return null;
        }
    }

    private void SaveDiskCache(byte[] rawBytes, Uri endpointUri, string? etag)
    {
        if (_options.CacheDirectory is null) return;

        Directory.CreateDirectory(_options.CacheDirectory);
        var indexPath = Path.Combine(_options.CacheDirectory, $"{Id}.index.json");
        WriteAtomicBytes(indexPath, rawBytes);
        SaveDiskCacheMetadata(endpointUri, etag);
    }

    private void SaveDiskCacheMetadata(Uri endpointUri, string? etag)
    {
        if (_options.CacheDirectory is null || string.IsNullOrWhiteSpace(etag)) return;

        Directory.CreateDirectory(_options.CacheDirectory);
        var metadataPath = Path.Combine(_options.CacheDirectory, $"{Id}.cache.json");
        var metadata = new HttpModIndexCacheMetadata
        {
            ETag = etag,
            EndpointUri = endpointUri.AbsoluteUri,
            FetchedAtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        var json = JsonSerializer.SerializeToUtf8Bytes(metadata, HostingJsonSerializerContext.DefaultOptions.HttpModIndexCacheMetadata);
        WriteAtomicBytes(metadataPath, json);
    }

    private static void WriteAtomicBytes(string destinationPath, byte[] bytes)
    {
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
            }

            File.Move(tempPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    private static Uri[] ParseMirrorUris(string[] mirrors)
    {
        if (mirrors.Length == 0) return [];

        var uris = new List<Uri>(mirrors.Length);
        foreach (var mirror in mirrors)
        {
            if (Uri.TryCreate(mirror, UriKind.Absolute, out var uri))
            {
                ModValidationRules.ValidateHttpUri(uri.AbsoluteUri);
                uris.Add(uri);
            }
        }

        return [.. uris];
    }

    private static Exception SelectBestError(ConcurrentQueue<Exception> errors)
    {
        ModValidationException? trustError = null;
        ModValidationException? validationError = null;
        Exception? firstError = null;

        foreach (var error in errors)
        {
            firstError ??= error;
            if (error is ModValidationException validation)
            {
                validationError ??= validation;
                if (validation.Code is "PublicKeyMismatch" or "UntrustedIndex" or "InvalidSignature")
                {
                    trustError ??= validation;
                }
            }
        }

        return trustError ?? validationError ?? firstError ?? new HttpRequestException("All repository endpoints failed.");
    }

    private static bool UriEquals(Uri left, Uri right) => string.Equals(left.AbsoluteUri, right.AbsoluteUri, StringComparison.Ordinal);

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class EndpointRaceCoordinator(
        int remaining,
        CachedIndexState? diskCache,
        CancellationTokenSource raceCancellation,
        TaskCompletionSource<FetchedIndexResult> winner,
        CancellationToken externalCancellation
    )
    {
        internal CachedIndexState? DiskCache { get; } = diskCache;

        internal CancellationTokenSource RaceCancellation { get; } = raceCancellation;

        internal TaskCompletionSource<FetchedIndexResult> Winner { get; } = winner;

        internal CancellationToken ExternalCancellation { get; } = externalCancellation;

        internal ConcurrentQueue<Exception> Errors { get; } = new();

        private int _remaining = remaining;

        internal int DecrementRemaining() => Interlocked.Decrement(ref _remaining);
    }

    private sealed record FetchedIndexResult(ModIndex Index, Uri EndpointUri, string? ETag, byte[]? RawBytes);

    private sealed record CachedIndexState(ModIndex Index, string ETag, Uri EndpointUri);
}

internal sealed record HttpModIndexCacheMetadata
{
    public required string ETag { get; init; }

    public required string EndpointUri { get; init; }

    public long FetchedAtUnixMs { get; init; }
}

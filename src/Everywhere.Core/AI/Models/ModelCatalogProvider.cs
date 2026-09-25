using System.Net;
using System.Security.Authentication;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

public sealed record ModelCatalogCacheEntry<TCatalog>(
    int Version,
    string? EntityTag,
    TCatalog Catalog
) where TCatalog : class;

public interface IModelCatalogCacheStore<TCatalog> where TCatalog : class
{
    ValueTask<ModelCatalogCacheEntry<TCatalog>?> LoadAsync(CancellationToken cancellationToken);

    ValueTask SaveAsync(ModelCatalogCacheEntry<TCatalog> entry, CancellationToken cancellationToken);

    ValueTask ClearAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns the refresh transaction shared by model catalog providers: cache restoration, request
/// coalescing, conditional fetches, retry timing, atomic cache revisions, and refresh lifetime.
/// It has no UI thread affinity; consumers marshal its notifications at their own boundary.
/// </summary>
public abstract partial class ModelCatalogProvider<TCatalog, TPrepared>(
    ModelCatalogHttpClient<TCatalog> client,
    IModelCatalogCacheStore<TCatalog> cacheStore,
    ILogger logger
) : ObservableObject, IDisposable where TCatalog : class where TPrepared : class
{
    [ObservableProperty]
    public partial bool IsRefreshing { get; private set; }

    protected abstract int CacheVersion { get; }

    protected virtual bool CanRefresh => true;

    private const int AutomaticRefreshAttempts = 3;
    private static TimeSpan SuccessfulRefreshCooldown => TimeSpan.FromSeconds(10);
    private static TimeSpan FailedRefreshCooldown => TimeSpan.FromSeconds(3);
    private static TimeSpan RateLimitCooldown => TimeSpan.FromMinutes(1);

    protected bool IsDisposed => Volatile.Read(ref _disposeState) != 0;

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();
    // Serializes cache writes, invalidation, and publication so an obsolete revision cannot
    // publish after a newer revision has cleared the catalog.
    private readonly SemaphoreSlim _catalogMutationLock = new(1, 1);

    protected readonly ILogger _logger = logger;

    private PreparedCache? _cache;
    private Task? _cacheLoadTask;
    private Task _cacheInvalidationTask = Task.CompletedTask;
    private RefreshOperation? _refreshOperation;
    private CancellationTokenSource _revisionCancellation = new();
    private DateTimeOffset _nextAutomaticRefreshAt;
    private DateTimeOffset _serverRetryAfter;
    private long _revision;

    private int _disposeState;

    /// <summary>
    /// Restores usable cached data without asserting that the source is currently authoritative.
    /// </summary>
    public async Task RestoreCacheAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetOrStartCacheLoad().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Performs a user-requested refresh. It bypasses local cooldowns, joins an active request, and
    /// still honors Retry-After supplied by the source.
    /// </summary>
    public async Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var error = await GetOrStartRefresh(force: true, retry: false).WaitAsync(cancellationToken);
            if (error is null || exceptionHandler is null || IsDisposed) return;

            exceptionHandler.HandleException(HandledSystemException.Handle(error));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>
    /// Invalidates the complete cached revision. Any fetch that started before this call is prevented
    /// from storing or publishing its result.
    /// </summary>
    public async Task InvalidateAsync(CancellationToken cancellationToken = default)
    {
        Task invalidation;
        CancellationTokenSource obsoleteRevision;
        lock (_stateLock)
        {
            if (IsDisposed) return;

            obsoleteRevision = _revisionCancellation;
            _revisionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var revision = ++_revision;
            _cache = null;
            var pendingCacheLoad = _cacheLoadTask;
            var previousInvalidation = _cacheInvalidationTask;
            _cacheLoadTask = Task.CompletedTask;
            _nextAutomaticRefreshAt = DateTimeOffset.MinValue;
            _serverRetryAfter = DateTimeOffset.MinValue;
            IsRefreshing = false;
            invalidation = _cacheInvalidationTask = Task.Run(
                () =>
                    InvalidateCacheCoreAsync(previousInvalidation, pendingCacheLoad, revision, _lifetime.Token),
                cancellationToken);
        }

        await obsoleteRevision.CancelAsync();
        obsoleteRevision.Dispose();
        try
        {
            await invalidation.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task InvalidateCacheCoreAsync(
        Task previousInvalidation,
        Task? pendingCacheLoad,
        long revision,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            await previousInvalidation;
            if (pendingCacheLoad is not null) await pendingCacheLoad;

            await _catalogMutationLock.WaitAsync(cancellationToken);
            try
            {
                await cacheStore.ClearAsync(cancellationToken);
            }
            finally
            {
                _catalogMutationLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear the model catalog cache");
        }

        try
        {
            await _catalogMutationLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            lock (_stateLock)
            {
                if (!IsCurrentRevisionUnsafe(revision)) return;
            }
            ClearPublishedCatalog();
            NotifyCatalogChanged();
        }
        finally
        {
            _catalogMutationLock.Release();
        }
    }

    private Task GetOrStartCacheLoad()
    {
        lock (_stateLock)
        {
            if (IsDisposed) return Task.CompletedTask;
            return _cacheLoadTask ??= Task.Run(() => LoadCacheCoreAsync(_revision, _revisionCancellation.Token));
        }
    }

    private async Task LoadCacheCoreAsync(long revision, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            var entry = await cacheStore.LoadAsync(cancellationToken);
            if (entry is null || entry.Version != CacheVersion || !IsCurrentRevision(revision)) return;

            var prepared = PrepareCatalog(entry.Catalog);
            await _catalogMutationLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRevision(revision)) return;
                ApplyCatalog(prepared, isAuthoritative: false);
                lock (_stateLock) _cache = new PreparedCache(entry, prepared);
                NotifyCatalogChanged();
            }
            finally
            {
                _catalogMutationLock.Release();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ignoring invalid model catalog cache");
        }
    }

    private Task<Exception?> GetOrStartRefresh(bool force, bool retry)
    {
        lock (_stateLock)
        {
            if (IsDisposed || !CanRefresh)
            {
                return Task.FromResult<Exception?>(null);
            }
            if (_refreshOperation is { Task.IsCompleted: false } active && active.Revision == _revision)
            {
                return active.Task;
            }
            if (!force && DateTimeOffset.UtcNow < _nextAutomaticRefreshAt)
            {
                return Task.FromResult<Exception?>(null);
            }

            var revision = _revision;
            var cancellationToken = _revisionCancellation.Token;
            var task = Task.Run(() => RunRefreshAsync(retry, revision, cancellationToken), cancellationToken);
            _refreshOperation = new RefreshOperation(revision, task);
            return task;
        }
    }

    private async Task<Exception?> RunRefreshAsync(bool retry, long revision, CancellationToken cancellationToken)
    {
        // Prevent callbacks from running while GetOrStartRefresh still owns the state lock.
        await Task.Yield();
        SetIsRefreshing(true, revision);
        try
        {
            await GetPendingInvalidation();
            await GetOrStartCacheLoad();
            cancellationToken.ThrowIfCancellationRequested();
            for (var attempt = 0;; attempt++)
            {
                await WaitForServerRetryAsync(cancellationToken);
                try
                {
                    await FetchCommitAndPublishAsync(revision, cancellationToken);
                    if (IsCurrentRevision(revision))
                    {
                        SetNextAutomaticRefresh(DateTimeOffset.UtcNow + SuccessfulRefreshCooldown, revision);
                    }

                    return null;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                catch (Exception ex)
                {
                    if (!IsCurrentRevision(revision)) return null;

                    CaptureServerRetry(ex, revision);
                    if (retry && attempt + 1 < AutomaticRefreshAttempts && IsTransientException(ex))
                    {
                        _logger.LogWarning(ex, "Could not refresh the model catalog; retrying");
                        await WaitForRetryBackoffAsync(attempt, cancellationToken);
                        continue;
                    }

                    if (TryHandleFinalFailure(ex, revision)) return null;
                    if (!IsCurrentRevision(revision)) return null;

                    _logger.LogWarning(ex, "Could not refresh the model catalog; all retries exhausted");
                    SetNextAutomaticRefresh(DateTimeOffset.UtcNow + FailedRefreshCooldown, revision);
                    return ex;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            SetIsRefreshing(false, revision);
        }
    }

    private async Task FetchCommitAndPublishAsync(long revision, CancellationToken cancellationToken)
    {
        PreparedCache? cache;
        lock (_stateLock) cache = _cache;

        var result = await client.FetchAsync(cache?.Entry.EntityTag, cancellationToken);
        if (!IsCurrentRevision(revision)) return;

        if (result is ModelCatalogFetchResult<TCatalog>.NotModified)
        {
            if (cache is null)
            {
                throw new InvalidDataException("The server returned Not Modified without a cached model catalog.");
            }

            await _catalogMutationLock.WaitAsync(cancellationToken);
            try
            {
                if (!IsCurrentRevision(revision)) return;
                ApplyCatalog(cache.Prepared, isAuthoritative: true);
                ClearServerRetry(revision);
                NotifyCatalogChanged();
            }
            finally
            {
                _catalogMutationLock.Release();
            }
            return;
        }

        var modified = (ModelCatalogFetchResult<TCatalog>.Modified)result;
        var updated = new ModelCatalogCacheEntry<TCatalog>(CacheVersion, modified.EntityTag, modified.Catalog);
        var preparedCatalog = PrepareCatalog(updated.Catalog);

        await _catalogMutationLock.WaitAsync(cancellationToken);
        try
        {
            if (!IsCurrentRevision(revision)) return;

            ApplyCatalog(preparedCatalog, isAuthoritative: true);
            lock (_stateLock)
            {
                if (!IsCurrentRevisionUnsafe(revision)) return;
                _cache = new PreparedCache(updated, preparedCatalog);
                _serverRetryAfter = DateTimeOffset.MinValue;
            }

            NotifyCatalogChanged();

            try
            {
                await cacheStore.SaveAsync(updated, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist the model catalog cache");
            }
        }
        finally
        {
            _catalogMutationLock.Release();
        }
    }

    private bool TryHandleFinalFailure(Exception exception, long revision)
    {
        return IsCurrentRevision(revision) && HandleFinalFailure(exception);
    }

    private async Task WaitForServerRetryAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset retryAfter;
        lock (_stateLock) retryAfter = _serverRetryAfter;

        var delay = retryAfter - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
    }

    private Task GetPendingInvalidation()
    {
        lock (_stateLock) return _cacheInvalidationTask;
    }

    private async Task WaitForRetryBackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var backoffUntil = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(Math.Pow(2, attempt + 1) + Random.Shared.NextDouble());
        DateTimeOffset retryAfter;
        lock (_stateLock) retryAfter = _serverRetryAfter;

        var nextAttempt = retryAfter > backoffUntil ? retryAfter : backoffUntil;
        var delay = nextAttempt - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
    }

    private void CaptureServerRetry(Exception exception, long revision)
    {
        var retryAfter = exception switch
        {
            ModelCatalogHttpException { RetryAfter: { } value } => value,
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } => DateTimeOffset.UtcNow + RateLimitCooldown,
            _ => DateTimeOffset.MinValue
        };
        if (retryAfter == DateTimeOffset.MinValue) return;

        lock (_stateLock)
        {
            if (!IsCurrentRevisionUnsafe(revision)) return;
            if (retryAfter > _serverRetryAfter) _serverRetryAfter = retryAfter;
        }
    }

    private void ClearServerRetry(long revision)
    {
        lock (_stateLock)
        {
            if (IsCurrentRevisionUnsafe(revision)) _serverRetryAfter = DateTimeOffset.MinValue;
        }
    }

    private bool IsCurrentRevision(long revision)
    {
        lock (_stateLock) return IsCurrentRevisionUnsafe(revision);
    }

    private bool IsCurrentRevisionUnsafe(long revision) => !IsDisposed && revision == _revision;

    private void SetNextAutomaticRefresh(DateTimeOffset value, long revision)
    {
        lock (_stateLock)
        {
            if (IsCurrentRevisionUnsafe(revision)) _nextAutomaticRefreshAt = value;
        }
    }

    private void SetIsRefreshing(bool value, long revision)
    {
        lock (_stateLock)
        {
            if (IsCurrentRevisionUnsafe(revision)) IsRefreshing = value;
        }
    }

    private static bool IsTransientException(Exception exception) =>
        exception is TaskCanceledException || exception is HttpRequestException http &&
        (http.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            http.StatusCode is HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout ||
            http.StatusCode is null && http.HttpRequestError != HttpRequestError.SecureConnectionError &&
            http.InnerException is not AuthenticationException);

    /// <summary>
    /// Starts an automatic refresh. Repeated triggers in the same revision join the active request.
    /// </summary>
    protected void RefreshInBackground() =>
        GetOrStartRefresh(force: false, retry: true).Detach(_logger.ToExceptionHandler());

    /// <summary>
    /// Validates and converts source data without changing published state. Runs off the UI thread.
    /// </summary>
    protected abstract TPrepared PrepareCatalog(TCatalog catalog);

    /// <summary>
    /// Replaces the published snapshot without raising change notifications. The caller serializes
    /// this operation with cache acceptance and invalidation.
    /// </summary>
    protected abstract void ApplyCatalog(TPrepared catalog, bool isAuthoritative);

    protected abstract void ClearPublishedCatalog();

    /// <summary>Notifies consumers after the new snapshot and validator have been accepted.</summary>
    protected virtual void OnCatalogChanged() { }

    protected void RaiseCatalogChanged(EventHandler? subscribers)
    {
        if (subscribers is null) return;
        foreach (var handler in subscribers.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model catalog subscriber failed");
            }
        }
    }

    private void NotifyCatalogChanged()
    {
        // Notifications are outside the catalog transaction: a broken subscriber must not reject
        // an otherwise valid body or prevent its ETag from being accepted.
        try
        {
            OnCatalogChanged();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Model catalog notification failed");
        }
    }

    protected virtual bool HandleFinalFailure(Exception exception) => false;

    public void Dispose()
    {
        Task? pendingCacheLoad;
        Task pendingInvalidation;
        Task<Exception?>? pendingRefresh;
        CancellationTokenSource revisionCancellation;
        lock (_stateLock)
        {
            if (IsDisposed) return;
            Volatile.Write(ref _disposeState, 1);
            _revision++;
            pendingCacheLoad = _cacheLoadTask;
            pendingInvalidation = _cacheInvalidationTask;
            pendingRefresh = _refreshOperation?.Task;
            revisionCancellation = _revisionCancellation;
        }

        revisionCancellation.Cancel();
        _lifetime.Cancel();
        ReleaseLifetimeAsync(pendingCacheLoad, pendingInvalidation, pendingRefresh, revisionCancellation).Detach(_logger.ToExceptionHandler());
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases derived resources after all pending catalog operations have stopped.</summary>
    protected virtual void DisposeCore() { }

    private async Task ReleaseLifetimeAsync(
        Task? pendingCacheLoad,
        Task pendingInvalidation,
        Task<Exception?>? pendingRefresh,
        CancellationTokenSource revisionCancellation)
    {
        try
        {
            var tasks = new List<Task>(3) { pendingInvalidation };
            if (pendingCacheLoad is not null) tasks.Add(pendingCacheLoad);
            if (pendingRefresh is not null) tasks.Add(pendingRefresh);
            await Task.WhenAll(tasks);
        }
        finally
        {
            try
            {
                DisposeCore();
            }
            finally
            {
                revisionCancellation.Dispose();
                _lifetime.Dispose();
                _catalogMutationLock.Dispose();
            }
        }
    }

    private sealed record RefreshOperation(long Revision, Task<Exception?> Task);

    private sealed record PreparedCache(ModelCatalogCacheEntry<TCatalog> Entry, TPrepared Prepared);
}
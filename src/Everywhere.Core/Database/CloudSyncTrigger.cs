namespace Everywhere.Database;

/// <summary>
/// Provides a static signaling mechanism for notifying the cloud synchronizer
/// that local data has changed and a sync cycle should be initiated promptly.
/// Static because <see cref="SyncSaveChangesInterceptor"/> is instantiated via <c>new</c> without DI access.
/// </summary>
public static class CloudSyncTrigger
{
    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static int _isSignaled;

    /// <summary>
    /// Signals that local syncable data has been modified.
    /// Called by <see cref="SyncSaveChangesInterceptor"/> after assigning version numbers.
    /// </summary>
    public static void SignalLocalChange()
    {
        if (Interlocked.Exchange(ref _isSignaled, 1) == 0)
        {
            Signal.Release();
        }
    }

    /// <summary>
    /// Asynchronously waits until a local change is signaled or cancellation is requested.
    /// </summary>
    public static async Task WaitForChangeAsync(CancellationToken cancellationToken = default)
    {
        await Signal.WaitAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _isSignaled, 0);
    }
}

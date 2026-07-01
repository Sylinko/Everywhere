namespace Everywhere.Extensions;

public static class LockExtensions
{
    /// <param name="semaphore">The SemaphoreSlim instance.</param>
    extension(SemaphoreSlim semaphore)
    {
        /// <summary>
        /// Asynchronously waits to enter the SemaphoreSlim and returns a disposable struct to automatically release the lock.
        /// </summary>
        /// <param name="cancellationToken">The CancellationToken to observe.</param>
        /// <returns>A ValueTask containing the SemaphoreReleaser struct.</returns>
        public async ValueTask<SemaphoreReleaser> LockAsync(CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(semaphore);

            var waitTask = semaphore.WaitAsync(cancellationToken);

            // Fast-path optimization (Zero Extra Allocation)
            // If the semaphore is available immediately, the task completes synchronously.
            // We can return the struct directly without entering the async state machine.
            if (waitTask.IsCompletedSuccessfully)
            {
                return new SemaphoreReleaser(semaphore);
            }

            // Slow-path (Asynchronous Wait)
            await waitTask.ConfigureAwait(false);

            return new SemaphoreReleaser(semaphore);
        }

        /// <summary>
        /// Asynchronously waits to enter the SemaphoreSlim with a specified timeout.
        /// </summary>
        /// <param name="timeout">A TimeSpan that represents the number of milliseconds to wait.</param>
        /// <param name="cancellationToken">The CancellationToken to observe.</param>
        /// <returns>A ValueTask containing the SemaphoreReleaser struct.</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout period.</exception>
        public async ValueTask<SemaphoreReleaser> LockAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(semaphore);

            var waitTask = semaphore.WaitAsync(timeout, cancellationToken);

            // Fast-path check for the timeout overload.
            if (waitTask.IsCompletedSuccessfully)
            {
                if (waitTask.Result)
                {
                    return new SemaphoreReleaser(semaphore);
                }

                // Fail fast if the timeout occurred synchronously.
                throw new TimeoutException("Failed to acquire the lock within the specified timeout.");
            }

            // Slow-path await.
            var acquired = await waitTask.ConfigureAwait(false);
            if (!acquired)
            {
                // By throwing an exception on timeout, we strictly prevent the caller
                // from accidentally entering the 'using' block (critical section) without holding the lock.
                throw new TimeoutException("Failed to acquire the lock within the specified timeout.");
            }

            return new SemaphoreReleaser(semaphore);
        }

        /// <summary>
        /// Synchronously waits to enter the SemaphoreSlim and returns a disposable struct to automatically release the lock.
        /// </summary>
        /// <returns></returns>
        public SemaphoreReleaser Lock()
        {
            ArgumentNullException.ThrowIfNull(semaphore);

            semaphore.Wait();
            return new SemaphoreReleaser(semaphore);
        }

        /// <summary>
        /// Synchronously waits to enter the SemaphoreSlim with a specified timeout.
        /// </summary>
        /// <param name="timeout">A TimeSpan that represents the number of milliseconds to wait.</param>
        /// <returns>A SemaphoreReleaser struct.</returns>
        /// <exception cref="TimeoutException">Thrown when the lock cannot be acquired within the timeout period.</exception>
        public SemaphoreReleaser Lock(TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(semaphore);

            if (!semaphore.Wait(timeout))
            {
                throw new TimeoutException("Failed to acquire the lock within the specified timeout.");
            }

            return new SemaphoreReleaser(semaphore);
        }
    }

    /// <summary>
    /// A disposable struct that releases the underlying SemaphoreSlim when disposed.
    /// Designed as a struct to maintain memory locality when hoisted into an async state machine.
    /// </summary>
    public struct SemaphoreReleaser : IDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal SemaphoreReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        /// <summary>
        /// Releases the SemaphoreSlim. This method is idempotent.
        /// </summary>
        public void Dispose()
        {
            // Use Interlocked.Exchange to atomically retrieve the reference and set it to null.
            // This guarantees that even if Dispose() is called multiple times, the underlying
            // SemaphoreSlim.Release() is only invoked once, preventing a SemaphoreFullException.
            Interlocked.Exchange(ref _semaphore, null)?.Release();
        }
    }
}
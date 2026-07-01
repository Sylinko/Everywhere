#if !NET5_0_OR_GREATER
using TaskCompletionSource = System.Threading.Tasks.TaskCompletionSource<object?>;
#endif

namespace Everywhere.Extensions;

public static class AsyncExtensions
{
    extension(CancellationToken cancellationToken)
    {
        public Task AsTask()
        {
            var tcs = new TaskCompletionSource();
            cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken), useSynchronizationContext: false);
            return tcs.Task;
        }

        public TaskAwaiter GetAwaiter()
        {
            return cancellationToken.AsTask().GetAwaiter();
        }
    }

    extension(WaitHandle handle)
    {
        public Task AsTask()
        {
            var tcs = new TaskCompletionSource();
            ThreadPool.RegisterWaitForSingleObject(
                handle,
#if NET5_0_OR_GREATER
                (_, _) => tcs.TrySetResult(),
#else
                (_, _) => tcs.TrySetResult(null),
#endif
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);

            return tcs.Task;
        }

        public TaskAwaiter GetAwaiter()
        {
            return handle.AsTask().GetAwaiter();
        }
    }
}
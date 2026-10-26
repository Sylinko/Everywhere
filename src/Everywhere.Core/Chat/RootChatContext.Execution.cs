namespace Everywhere.Chat;

partial class RootChatContext
{
    private readonly ConcurrentExclusiveSchedulerPair _execution = new(TaskScheduler.Default, maxConcurrencyLevel: 1);

    /// <summary>
    /// Serializes a short synchronous model operation with all operations on this root and its
    /// nested conversations. Network, tool, and database waits happen outside this domain.
    /// Admission of long-running work is separate: cancellation cleanup and persistence captures
    /// still use the domain after StopAsync has stopped accepting new generations.
    /// </summary>
    internal Task<T> EnqueueAsync<T>(Func<T> action)
    {
        if (TaskScheduler.Current == _execution.ExclusiveScheduler)
        {
            return Task.FromResult(action());
        }

        return Task.Factory.StartNew(
            action,
            CancellationToken.None,
            TaskCreationOptions.DenyChildAttach,
            _execution.ExclusiveScheduler);
    }

    internal Task EnqueueAsync(Action action)
    {
        return EnqueueAsync(() =>
        {
            action();
            return true;
        });
    }
}
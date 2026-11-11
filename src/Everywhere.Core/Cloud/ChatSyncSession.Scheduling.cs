using Everywhere.Database;

namespace Everywhere.Cloud;

partial class ChatSyncSession
{
    // Exactly two readers own every transfer, including newly restarted chats. A new
    // chat can use a free slot while a different transfer or discovery pass is stalled.
    // Per-chat completion coalesces admissions; there is no general-purpose dispatcher.
    private Task QueueChat(Guid id, bool shouldRetryQuota, CancellationToken token, bool hasNewChanges = false)
    {
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested || token.IsCancellationRequested)
            {
                return Task.FromCanceled(_lifetime.IsCancellationRequested ? _lifetime.Token : token);
            }

            var work = GetWork(id);
            work.ShouldRetryQuota |= shouldRetryQuota;
            if (work.Queued is { } queued)
            {
                if (hasNewChanges)
                {
                    work.ShouldRunAgain = true;
                    work.QueueToken = token;
                }
                return queued.Task;
            }

            work.Queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            work.QueueToken = token;
            if (!_queue.Writer.TryWrite(id))
            {
                throw new OperationCanceledException(_lifetime.Token);
            }

            return work.Queued.Task;
        }
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                var work = GetWork(id);
                TaskCompletionSource completion;
                CancellationToken token;
                bool shouldRetry;
                lock (_gate)
                {
                    completion = work.Queued ?? throw new InvalidOperationException("A queued chat has no completion owner.");
                    token = work.QueueToken;
                    shouldRetry = work.ShouldRetryQuota;
                    work.ShouldRetryQuota = false;
                }

                Exception? failure = null;
                try
                {
                    await TransferChatAsync(id, shouldRetry, token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failure = error;
                }

                lock (_gate)
                {
                    // A restart may arrive while an old attempt is draining. Its new
                    // intent receives one more admission, never the old attempt's result.
                    if (work.ShouldRunAgain && !_lifetime.IsCancellationRequested)
                    {
                        work.ShouldRunAgain = false;
                        _queue.Writer.TryWrite(id);
                        continue;
                    }

                    work.Queued = null;
                }

                if (failure is OperationCanceledException canceled)
                {
                    completion.TrySetCanceled(canceled.CancellationToken);
                }
                else if (failure is not null)
                {
                    completion.TrySetException(failure);
                }
                else
                {
                    completion.TrySetResult();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private IQueryable<Guid> PendingChatIds(ChatV2DbContext db)
    {
        return db.SyncChats
            .Where(row => row.AccountId == AccountId &&
                !db.Chats.Any(chat => chat.Id == row.ChatId && chat.IsSyncExcluded) &&
                (row.DiscoveredRevision > row.AppliedRevision ||
                    db.SyncRemote.Any(remote =>
                        remote.AccountId == AccountId && remote.ChatId == row.ChatId && remote.Revision > row.AppliedRevision) ||
                    db.SyncSubmissions.Any(submission => submission.AccountId == AccountId && submission.ChatId == row.ChatId) ||
                    db.SyncPending.Any(pending => pending.AccountId == AccountId && pending.ChatId == row.ChatId &&
                        (!row.IsContentUnavailable || pending.Kind == ChatSyncRecordKind.Lifecycle && pending.Key == "republish"))))
            .Select(row => row.ChatId);
    }
}
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

partial class ChatSyncSession
{
    private async Task TransferChatAsync(Guid id, bool shouldRetryQuota, CancellationToken passToken)
    {
        var work = GetWork(id);
        await work.Gate.WaitAsync(passToken).ConfigureAwait(false);
        CancellationTokenSource? transferCancellation = null;
        try
        {
            await using (var db = await _dbFactory.CreateDbContextAsync(passToken).ConfigureAwait(false))
            {
                if (!await PendingChatIds(db).ContainsAsync(id, passToken).ConfigureAwait(false))
                {
                    return;
                }
            }

            lock (_gate)
            {
                if (work.Operation != ChatSyncOperation.None)
                {
                    return;
                }

                transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(passToken);
                work.Transfer = transferCancellation;
            }

            PublishInventory();
            var token = transferCancellation.Token;
            await SynchronizeChatAsync(id, shouldRetryQuota, token).ConfigureAwait(false);
            lock (_gate)
            {
                _errors.Remove(id);
            }
        }
        catch (OperationCanceledException) when (!passToken.IsCancellationRequested && transferCancellation?.IsCancellationRequested == true)
        {
            // A participation command interrupted only this chat. Its frozen upload is
            // still durable; the management path decides whether it can be withdrawn.
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            lock (_gate)
            {
                _errors[id] = error;
            }
            if (error is not ChatSyncException { Failure.Code: ChatSyncFailureCode.QuotaExceeded })
            {
                _logger.LogWarning(error, "Chat sync failed for {ChatId}; durable work is retained.", id);
            }
        }
        finally
        {
            lock (_gate)
            {
                work.Transfer = null;
            }

            transferCancellation?.Dispose();
            work.Gate.Release();
            if (!passToken.IsCancellationRequested)
            {
                await ReloadItemsAsync(id, passToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SynchronizeChatAsync(Guid id, bool shouldRetryQuota, CancellationToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        if (await db.Chats.AnyAsync(chat => chat.Id == id && !chat.IsTemporary && chat.LocalAccountId != AccountId, token).ConfigureAwait(false))
        {
            throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
        }

        var transfer = new ChatSyncTransfer(_dbFactory, _transport);
        var submission = await db.SyncSubmissions
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AccountId == AccountId && row.ChatId == id, token)
            .ConfigureAwait(false);
        if (submission is { QuotaRejection: null })
        {
            // The first action for this chat resolves its exact previous request, never
            // captures replacement bytes against a potentially unacknowledged commit.
            try
            {
                RecordReceipt(await transfer.UploadAsync(id, token).ConfigureAwait(false));
            }
            catch (ChatSyncException error) when (NeedsRebase(error))
            {
                if (await ReceiveAsync(id, true, token).ConfigureAwait(false) != ReceiveResult.Current)
                {
                    return;
                }
            }
            catch (ChatSyncException error) when (error.Failure.Code == ChatSyncFailureCode.QuotaExceeded)
            {
                // The durable rejection below controls admission of a new capture.
            }
        }

        var received = await ReceiveAsync(id, false, token).ConfigureAwait(false);
        if (received == ReceiveResult.RebuildDeferred)
        {
            return;
        }

        submission = await db.SyncSubmissions
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AccountId == AccountId && row.ChatId == id, token)
            .ConfigureAwait(false);
        if (submission?.QuotaRejection is { } rejection)
        {
            var failure = MessagePackSerializer.Deserialize<ChatSyncFailure>(rejection, cancellationToken: token);
            var hasLifecycle = await (
                from pending in db.SyncPending
                join chat in db.SyncChats
                    on new { pending.AccountId, pending.ChatId } equals new { chat.AccountId, chat.ChatId }
                where pending.AccountId == AccountId && pending.ChatId == id &&
                    pending.Kind == ChatSyncRecordKind.Lifecycle && pending.Key != "republish" &&
                    (pending.Key == "delete" || chat.AppliedRevision > 0)
                select pending.ChatId).AnyAsync(token).ConfigureAwait(false);

            ChatSyncStorageUsage? usage;
            lock (_gate)
            {
                usage = _usage;
            }

            var hasMoreSpace = usage is not null && failure.Quota?.Usage is { } rejected &&
                (usage.UsedBytes < rejected.UsedBytes || usage.LimitBytes > rejected.LimitBytes);
            if (!shouldRetryQuota && !hasLifecycle && !hasMoreSpace)
            {
                throw new ChatSyncException(failure);
            }

            await _transport.DiscardUploadAsync(submission.CommitId, token).ConfigureAwait(false);
            await db.SyncSubmissions
                .Where(row => row.AccountId == AccountId && row.CommitId == submission.CommitId)
                .ExecuteDeleteAsync(token).ConfigureAwait(false);
        }

        var hasTransition = await db.SyncPending
            .AnyAsync(row => row.AccountId == AccountId && row.ChatId == id && row.Kind == ChatSyncRecordKind.Lifecycle, token)
            .ConfigureAwait(false);
        // A lifecycle transition may need a second capture to publish the final stable
        // graph. Each capture still acknowledges only its own saved local revisions.
        var captures = hasTransition && received == ReceiveResult.Current ? 2 : 1;
        for (var capture = 0; capture < captures; capture++)
        {
            await _manager.PrepareUploadAsync(AccountId, id, token).ConfigureAwait(false);
            try
            {
                RecordReceipt(await transfer.UploadAsync(id, token).ConfigureAwait(false));
            }
            catch (ChatSyncException error) when (NeedsRebase(error))
            {
                if (await ReceiveAsync(id, true, token).ConfigureAwait(false) != ReceiveResult.Current)
                {
                    return;
                }

                await _manager.PrepareUploadAsync(AccountId, id, token).ConfigureAwait(false);
                RecordReceipt(await transfer.UploadAsync(id, token).ConfigureAwait(false));
            }
            if (received == ReceiveResult.Busy)
            {
                return;
            }

            received = await ReceiveAsync(id, false, token).ConfigureAwait(false);
            if (received != ReceiveResult.Current)
            {
                return;
            }
        }
    }

    private async Task<ReceiveResult> ReceiveAsync(Guid id, bool shouldRebase, CancellationToken token)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var state = await db.SyncChats
            .AsNoTracking()
            .SingleAsync(row => row.AccountId == AccountId && row.ChatId == id, token)
            .ConfigureAwait(false);
        var hasReceipt = await db.SyncRemote
            .AnyAsync(row => row.AccountId == AccountId && row.ChatId == id && row.Revision > state.AppliedRevision, token)
            .ConfigureAwait(false);
        if (!shouldRebase && state.DiscoveredRevision <= state.AppliedRevision && !hasReceipt)
        {
            return ReceiveResult.Current;
        }

        if (shouldRebase)
        {
            var commit = await db.SyncSubmissions
                .Where(row => row.AccountId == AccountId && row.ChatId == id)
                .Select(row => (Guid?)row.CommitId)
                .SingleOrDefaultAsync(token)
                .ConfigureAwait(false);
            if (commit is { } commitId)
            {
                await _transport.DiscardUploadAsync(commitId, token).ConfigureAwait(false);
            }
        }

        using var snapshot = await ChatSyncDownloadedSnapshot.ReadAsync(_transport, id, state.AppliedRevision, token).ConfigureAwait(false);
        if (!await _manager.CommitReceivedSnapshotAsync(AccountId, snapshot, shouldRebase, token).ConfigureAwait(false))
        {
            return snapshot.CrossesRepublish ? ReceiveResult.RebuildDeferred : ReceiveResult.Busy;
        }

        UpdateCopy(snapshot.Head);
        return ReceiveResult.Current;
    }

    private void RecordReceipt(ChatSyncCommitResult? receipt)
    {
        if (receipt is not null)
        {
            RecordUsage(receipt.Revision, new ChatSyncStorageUsage(receipt.UsedBytes, receipt.LimitBytes));
        }
    }

    private void UpdateCopy(ChatSyncHead head)
    {
        lock (_gate)
        {
            var id = Guid.ParseExact(head.ChatId, "D");
            if (_copies.TryGetValue(id, out var previous) && previous.Revision > head.Revision)
            {
                return;
            }

            if (head.Availability == ChatSyncAvailability.Available && head.Lifecycle != ChatSyncLifecycle.Deleted)
            {
                _copies[id] = new ChatSyncCopy(head.ChatId, _copies.GetValueOrDefault(id)?.Title, head.Bytes, head.Revision, head.LifecycleRevision);
            }
            else
            {
                _copies.Remove(id);
            }
        }
    }

    private static bool NeedsRebase(ChatSyncException error)
    {
        return error.Failure.Code is
            ChatSyncFailureCode.RevisionConflict or
            ChatSyncFailureCode.ChatDeleted or
            ChatSyncFailureCode.LifecycleConflict or
            ChatSyncFailureCode.ContentUnavailable;
    }

    private enum ReceiveResult
    {
        Current,
        Busy,
        RebuildDeferred
    }
}
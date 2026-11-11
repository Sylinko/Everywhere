using Everywhere.Database;
using Everywhere.Storage;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Cloud;

/// <summary>
/// Transfers durable frozen submissions for one account session. The session serializes
/// operations for each chat and owns scheduling, conflict reconciliation and cancellation.
/// Different chats may transfer concurrently. Network waits never hold a local transaction.
/// </summary>
public sealed class ChatSyncTransfer(IDbContextFactory<ChatV2DbContext> dbFactory, ChatSyncTransport transport)
{
    /// <summary>
    /// Uploads a chat's existing frozen request and acknowledges only its captured revisions.
    /// Cancellation, transport failures, and rejections leave the submission available for recovery.
    /// </summary>
    public async Task<ChatSyncCommitResult?> UploadAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var accountId = transport.AccountId;
        var account = await db.SyncAccounts.AsNoTracking().SingleAsync(row => row.AccountId == accountId, cancellationToken).ConfigureAwait(false);
        if (account.Generation != transport.Compatibility.SyncGeneration)
        {
            throw new InvalidOperationException("The sync session does not match the enrolled generation.");
        }

        var submission = await db.SyncSubmissions
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.AccountId == accountId && row.ChatId == chatId, cancellationToken)
            .ConfigureAwait(false);
        if (submission is null)
        {
            return null;
        }

        if (submission.QuotaRejection is { } rejected)
        {
            throw new ChatSyncException(MessagePackSerializer.Deserialize<ChatSyncFailure>(rejected, cancellationToken: cancellationToken));
        }

        var receipt = await transport.BeginAsync(submission.Header, cancellationToken).ConfigureAwait(false);
        if (receipt is null)
        {
            var count = await db.SyncParts
                .CountAsync(row => row.AccountId == accountId && row.ChatId == chatId, cancellationToken)
                .ConfigureAwait(false);
            var progress = await transport.ReadUploadProgressAsync(submission.CommitId, cancellationToken).ConfigureAwait(false);
            if (progress.NextPart < 0 || progress.NextPart > count)
            {
                throw new InvalidDataException("The server upload position exceeds the frozen submission.");
            }
            if (progress.NextPart < count)
            {
                await transport
                    .UploadPartsAsync(submission.CommitId, progress.NextPart, ReadPartsAsync(cancellationToken), cancellationToken)
                    .ConfigureAwait(false);
            }

            async IAsyncEnumerable<byte[]> ReadPartsAsync([EnumeratorCancellation] CancellationToken token)
            {
                // Authentication replay gets an independent reader over the same frozen source.
                await using var partsDb = await dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                for (var index = progress.NextPart; index < count; index++)
                {
                    // Close the SQLite reader before yielding to the network; do not materialize the whole submission.
                    var bytes = await partsDb.SyncParts
                        // ReSharper disable once AccessToModifiedClosure
                        .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.Position == index)
                        .Select(row => row.Body)
                        .SingleAsync(token)
                        .ConfigureAwait(false);
                    yield return bytes;
                }
            }

            try
            {
                receipt = await transport.CommitAsync(submission.CommitId, count, cancellationToken).ConfigureAwait(false);
            }
            catch (ChatSyncException exception) when (exception.Failure.Code == ChatSyncFailureCode.QuotaExceeded)
            {
                // The server has definitively rejected this submission. Preserve that result
                // even if the caller cancels now; cancellation cannot make its outcome uncertain.
                var rejection = MessagePackSerializer.Serialize(exception.Failure, cancellationToken: CancellationToken.None);
                await db.SyncSubmissions
                    .Where(row => row.AccountId == accountId && row.CommitId == submission.CommitId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.QuotaRejection, rejection), CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
        }

        if (receipt.CommitId != submission.CommitId.ToString("D"))
        {
            throw new InvalidDataException("The sync receipt identifies a different submission.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await new ChatSyncStateStore(db).AcknowledgeAsync(accountId, receipt, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return receipt;
    }

    /// <summary>
    /// Discovers and durably registers one page. A canceled transaction leaves the old cursor
    /// so the next session rediscovers the work; installation has its own applied revision.
    /// </summary>
    public async Task<ChatSyncDiscovery> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory
            .CreateDbContextAsync(cancellationToken)
            .ConfigureAwait(false);
        var account = await db.SyncAccounts
            .AsNoTracking()
            .SingleAsync(row => row.AccountId == transport.AccountId, cancellationToken)
            .ConfigureAwait(false);
        if (account.Generation != transport.Compatibility.SyncGeneration)
        {
            throw new InvalidOperationException("The sync session does not match the enrolled generation.");
        }

        var page = await transport.DiscoverAsync(account.ScanCursor, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await new ChatSyncStateStore(db).RecordDiscoveryAsync(account.AccountId, account.ScanCursor, page, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return page;
    }
}
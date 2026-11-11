using Everywhere.Cloud;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task ApplyClearedHeadAsync(string accountId, ChatSyncHead head, CancellationToken cancellationToken)
    {
        var id = Guid.ParseExact(head.ChatId, "D");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var owner = await db.Chats.Where(chat => chat.Id == id).Select(chat => chat.LocalAccountId).SingleOrDefaultAsync(cancellationToken);
        if (owner is not null && owner != accountId)
        {
            throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
        }

        var applied = await db.SyncChats
            .Where(chat => chat.AccountId == accountId && chat.ChatId == id)
            .Select(chat => (long?)chat.AppliedRevision)
            .SingleOrDefaultAsync(cancellationToken);
        if (applied >= head.Revision)
        {
            return;
        }

        await db.SyncPending
            .Where(row => row.AccountId == accountId && row.ChatId == id &&
                row.Kind == ChatSyncRecordKind.Lifecycle &&
                (row.Key != "republish" || row.BaseRevision != head.Revision))
            .ExecuteDeleteAsync(cancellationToken);
        await SaveHeadAsync(db, accountId, id, head, false, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Records explicit republication against the exact installed clear revision.
    /// </summary>
    public async Task RequestRepublishAsync(string accountId, Guid chatId, long revision, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var canStart = await db.SyncChats.AnyAsync(
            row => row.AccountId == accountId && row.ChatId == chatId &&
                row.IsContentUnavailable && row.AppliedRevision == revision && row.DiscoveredRevision <= revision,
            cancellationToken);
        var hasGraph = await db.Chats.AnyAsync(row => row.Id == chatId && !row.IsTemporary && row.LocalAccountId == accountId, cancellationToken);
        if (!canStart || !hasGraph)
        {
            throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
        }

        await new ChatSyncStateStore(db).RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Lifecycle, "republish", revision, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Withdraws a not-yet-published restart while preserving dormant content and field edits.
    /// An uncertain upload cannot be discarded as though it were rejected.
    /// </summary>
    public async Task WithdrawRepublishAsync(string accountId, Guid chatId, long revision, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var submissions = db.SyncSubmissions.Where(row => row.AccountId == accountId && row.ChatId == chatId);
        var canWithdraw = await db.SyncChats.AnyAsync(
            row => row.AccountId == accountId && row.ChatId == chatId &&
                row.IsContentUnavailable && row.AppliedRevision == revision && row.DiscoveredRevision <= revision,
            cancellationToken);
        if (!canWithdraw || await submissions.AnyAsync(row => row.QuotaRejection == null, cancellationToken))
        {
            throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
        }

        var removed = await db.SyncPending.Where(row =>
            row.AccountId == accountId && row.ChatId == chatId &&
            row.Kind == ChatSyncRecordKind.Lifecycle &&
            row.Key == "republish" &&
            row.BaseRevision == revision).ExecuteDeleteAsync(cancellationToken);
        if (removed == 0)
        {
            throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
        }

        await submissions.ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
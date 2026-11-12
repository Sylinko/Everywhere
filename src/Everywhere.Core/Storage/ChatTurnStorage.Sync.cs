using Everywhere.Cloud;
using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <summary>
    /// Account for newly retained conversations while this device's sync switch is on.
    /// Existing conversations keep their durable owner even after logout or account switching.
    /// </summary>
    public string? SyncAccountId { get; set; }

    private async Task RecordSyncChangesAsync(
        ChatV2DbContext db,
        ChatWriteBatch batch,
        ChatV2Entity? previous,
        string? retainedBy,
        CancellationToken cancellationToken)
    {
        if (batch.Metadata.Snapshot.IsTemporary)
        {
            return;
        }

        var chatId = batch.Metadata.Snapshot.Id;
        if (previous is { IsSyncExcluded: true })
        {
            return;
        }

        if (previous is null || previous.IsTemporary)
        {
            if (retainedBy is not null && retainedBy == SyncAccountId)
            {
                await RegisterGraphAsync(db, retainedBy, chatId, false, cancellationToken);
            }
            return;
        }
        if (previous.LocalAccountId is not { } accountId ||
            !await TrackedSyncStates(db).AnyAsync(state => state.AccountId == accountId && state.ChatId == chatId, cancellationToken))
        {
            return;
        }

        var store = new ChatSyncStateStore(db);
        foreach (var write in batch.Nodes)
        {
            var node = write.Topology;
            if (write.Payload is not null)
            {
                await RecordAsync(ChatSyncRecordKind.Node, node.Id.ToString("D"));
            }
            if ((write.Kind & ChatNodeChangeKind.Choice) != 0)
            {
                await RecordAsync(ChatSyncRecordKind.Choice, node.Id.ToString("D"));
            }
        }
        foreach (var field in batch.Metadata.ChangedFields)
        {
            await RecordAsync(ChatSyncRecordKind.Field, field.Key);
        }

        async Task RecordAsync(ChatSyncRecordKind kind, string key)
        {
            var baseline = await db.SyncRemote
                .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == kind && row.Key == key)
                .Select(row => (long?)row.Revision)
                .SingleOrDefaultAsync(cancellationToken) ?? 0;
            await store.RecordChangeAsync(accountId, chatId, kind, key, baseline, cancellationToken);
        }
    }

    /// <summary>
    /// Changes participation for a never-published graph in a single transaction.
    /// A cloud copy or an unresolved upload must be managed using its actual remote outcome.
    /// </summary>
    public async Task SetUnpublishedParticipationAsync(string accountId, Guid chatId, bool shouldSync, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var store = new ChatSyncStateStore(db);
        var local = await store.GetEligibleLocalChats(accountId).AsNoTracking().SingleOrDefaultAsync(row => row.Id == chatId, cancellationToken);
        if (local is null)
        {
            throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
        }
        if (local.LocalAccountId is null)
        {
            await db.Chats.Where(chat => chat.Id == chatId)
                .ExecuteUpdateAsync(update => update.SetProperty(chat => chat.LocalAccountId, accountId), cancellationToken);
        }

        var state = await db.SyncChats
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.ChatId == chatId && row.AccountId == accountId, cancellationToken);
        if (shouldSync && local.IsSyncExcluded)
        {
            await db.Chats
                .Where(chat => chat.Id == chatId)
                .ExecuteUpdateAsync(update => update.SetProperty(chat => chat.IsSyncExcluded, false), cancellationToken);
            if (state is null)
            {
                await RegisterGraphAsync(db, accountId, chatId, local.ArchivedAt.HasValue, cancellationToken);
            }

            // If a copy appeared elsewhere while excluded, discovery owns the first merge.
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (state is not null && (state.AppliedRevision != 0 || state.DiscoveredRevision != 0) ||
            await db.SyncRemote.AnyAsync(row => row.AccountId == accountId && row.ChatId == chatId, cancellationToken) ||
            await db.SyncSubmissions.AnyAsync(
                row => row.AccountId == accountId && row.ChatId == chatId && row.QuotaRejection == null,
                cancellationToken))
        {
            throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
        }

        if (shouldSync && state is null)
        {
            await RegisterGraphAsync(db, accountId, chatId, local.ArchivedAt.HasValue, cancellationToken);
        }
        else if (!shouldSync)
        {
            await db.Chats.Where(chat => chat.Id == chatId).ExecuteUpdateAsync(
                update => update.SetProperty(chat => chat.IsSyncExcluded, true),
                cancellationToken);
            await db.SyncSubmissions.Where(row => row.ChatId == chatId && row.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
            await db.SyncPending.Where(row => row.ChatId == chatId && row.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
            await db.SyncChats.Where(row => row.ChatId == chatId && row.AccountId == accountId).ExecuteDeleteAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Enrolls retained local graphs only after the complete account head list has been discovered.
    /// A revision for a cleared or deleted copy is just as authoritative as an available copy.
    /// </summary>
    internal static async Task EnrollLocalHistoryAsync(ChatV2DbContext db, string accountId, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        // Reserve matched graphs too. A first merge can fail on divergent bodies; switching
        // accounts afterwards must never enroll that still-unmerged graph for another subject.
        await new ChatSyncStateStore(db).GetEligibleLocalChats(accountId)
            .Where(chat => chat.LocalAccountId == null)
            .ExecuteUpdateAsync(update => update.SetProperty(chat => chat.LocalAccountId, accountId), token);

        var chats = await db.Chats
            .Where(chat =>
                chat.LocalAccountId == accountId && !chat.IsTemporary && !chat.IsSyncExcluded &&
                !db.SyncChats.Any(state => state.AccountId == accountId && state.ChatId == chat.Id))
            .Select(chat => new { chat.Id, IsArchived = chat.ArchivedAt.HasValue })
            .ToArrayAsync(token);
        foreach (var chat in chats)
        {
            await RegisterGraphAsync(db, accountId, chat.Id, chat.IsArchived, token);
        }
        await transaction.CommitAsync(token);
    }

    private static async Task RegisterGraphAsync(
        ChatV2DbContext db,
        string accountId,
        Guid chatId,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        var head = MessagePackSerializer.Serialize(ChatSyncHead.Initial(chatId), cancellationToken: cancellationToken);
        db.SyncChats.Add(new ChatSyncChatEntity { AccountId = accountId, ChatId = chatId, Head = head });
        await db.SaveChangesAsync(cancellationToken);

        // Select node IDs in SQLite; enrolling a large history never reads message bodies.
        // Metadata uses the same field-intent recorder as later edits.
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE SyncAccounts SET LocalRevision = LocalRevision + 1 WHERE AccountId = {accountId};
             INSERT INTO SyncPending (AccountId, ChatId, Kind, Key, LocalRevision, BaseRevision)
             SELECT {accountId}, {chatId}, edits.Kind, edits.Key, account.LocalRevision, 0
             FROM SyncAccounts AS account CROSS JOIN (
                 SELECT kinds.Kind, lower(n.Id) AS Key FROM Nodes AS n
                 CROSS JOIN (SELECT 'node' AS Kind UNION ALL SELECT 'choice') AS kinds
                 WHERE n.ChatContextId = {chatId}
             ) AS edits WHERE account.AccountId = {accountId}
             """,
            cancellationToken);
        var chat = await db.Chats.AsNoTracking().SingleAsync(row => row.Id == chatId, cancellationToken);
        var store = new ChatSyncStateStore(db);
        foreach (var key in ChatMetadataMapper.ReadFields(chat).Keys)
        {
            await store.RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Field, key, 0, cancellationToken);
        }
        if (isArchived)
        {
            await store.RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Lifecycle, "archive", 0, cancellationToken);
        }
    }

    private static async Task RecordSyncLifecycleAsync(ChatV2DbContext db, Guid chatId, ChatSyncAction action, CancellationToken cancellationToken)
    {
        var state = await TrackedLocalChat(db, chatId).SingleOrDefaultAsync(cancellationToken);
        if (state is null)
        {
            return;
        }

        var store = new ChatSyncStateStore(db);
        var accountId = state.AccountId;
        if (state.IsContentUnavailable)
        {
            if (action == ChatSyncAction.Delete)
            {
                // Drop this device's retained fields and edits together with its local graph.
                // Keep the applied head so discovery still observes a later explicit republish.
                await db.SyncPending.Where(row => row.AccountId == accountId && row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
                await db.SyncRemote.Where(row => row.AccountId == accountId && row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
                await db.SyncSubmissions.Where(row => row.AccountId == accountId && row.ChatId == chatId && row.QuotaRejection != null)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            return;
        }

        var lifecycleRevision = await db.SyncRemote
            .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == ChatSyncRecordKind.Lifecycle && row.Key == "state")
            .Select(row => (long?)row.Revision)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        // An explicit new decision replaces the preceding lifecycle intent, not an uncertain request.
        await db.SyncPending
            .Where(row => row.AccountId == accountId && row.ChatId == chatId &&
                (row.Kind == ChatSyncRecordKind.Lifecycle || action == ChatSyncAction.Delete))
            .ExecuteDeleteAsync(cancellationToken);
        await store.RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Lifecycle, action.ToStorageKey(), lifecycleRevision, cancellationToken);
    }

    private static async Task RecordSyncFieldAsync(ChatV2DbContext db, Guid chatId, string field, CancellationToken cancellationToken)
    {
        var accountId = await TrackedLocalChat(db, chatId).Select(state => state.AccountId).SingleOrDefaultAsync(cancellationToken);
        if (accountId is null)
        {
            return;
        }
        var baseline = await db.SyncRemote
            .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == ChatSyncRecordKind.Field && row.Key == field)
            .Select(row => (long?)row.Revision)
            .SingleOrDefaultAsync(cancellationToken) ?? 0;
        await new ChatSyncStateStore(db).RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Field, field, baseline, cancellationToken);
    }

    // Scope the head to the local owner first. A discovered head alone isn't a tracked
    // graph: until the first merge, local branches have no shared revision baseline.
    private static IQueryable<ChatSyncChatEntity> TrackedLocalChat(ChatV2DbContext db, Guid chatId)
    {
        return TrackedSyncStates(db).Where(state => state.ChatId == chatId &&
            db.Chats.Any(chat => chat.Id == chatId && chat.LocalAccountId == state.AccountId && !chat.IsSyncExcluded && !chat.IsTemporary));
    }

    private static IQueryable<ChatSyncChatEntity> TrackedSyncStates(ChatV2DbContext db)
    {
        return db.SyncChats.AsNoTracking().Where(state =>
            db.SyncPending.Any(row => row.AccountId == state.AccountId && row.ChatId == state.ChatId) ||
            db.SyncRemote.Any(row => row.AccountId == state.AccountId && row.ChatId == state.ChatId));
    }
}
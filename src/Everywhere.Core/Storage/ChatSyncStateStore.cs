using Everywhere.Cloud;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

/// <summary>
/// Durable sync bookkeeping on a caller-owned chat database transaction. Callers save
/// chat changes and upload intents, or remote records and progress, in that same transaction.
/// This class performs no network I/O and does not enroll or synchronize chats automatically.
/// </summary>
public sealed class ChatSyncStateStore(ChatV2DbContext db)
{
    /// <summary>
    /// Returns retained history owned by this account or available for it to adopt.
    /// Another account's durable state prevents adoption of an unattributed graph;
    /// a null local owner alone does not make that history available to this account.
    /// </summary>
    public IQueryable<ChatV2Entity> GetEligibleLocalChats(string accountId)
    {
        return db.Chats.Where(chat => !chat.IsTemporary &&
            (chat.LocalAccountId == accountId || chat.LocalAccountId == null &&
                !db.SyncChats.Any(state => state.ChatId == chat.Id && state.AccountId != accountId &&
                    (state.AppliedRevision > 0 ||
                        db.SyncRemote.Any(row => row.AccountId == state.AccountId && row.ChatId == state.ChatId) ||
                        db.SyncPending.Any(row => row.AccountId == state.AccountId && row.ChatId == state.ChatId)))));
    }

    /// <summary>
    /// Enrolls one account without attaching any existing local chat to it. A changed
    /// server generation requires explicit reconciliation, not an automatic cursor reset.
    /// </summary>
    public async Task EnrollAsync(string accountId, long generation, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlAsync(
            $"INSERT INTO SyncAccounts (AccountId, Generation, ScanCursor, LocalRevision) VALUES ({accountId}, {generation}, 0, 0) ON CONFLICT DO NOTHING",
            cancellationToken);
        var existing = await db.SyncAccounts.AsNoTracking().SingleAsync(row => row.AccountId == accountId, cancellationToken);
        if (existing.Generation != generation)
        {
            throw new InvalidOperationException("The account sync generation requires reconciliation.");
        }
    }

    /// <summary>
    /// Records an actual node, choice, field, or lifecycle edit. Coalescing retains the
    /// first unresolved remote baseline and advances only the local intent revision.
    /// </summary>
    public async Task RecordChangeAsync(
        string accountId,
        Guid chatId,
        ChatSyncRecordKind kind,
        string key,
        long baseRevision,
        CancellationToken cancellationToken = default)
    {
        var affected = await db.SyncAccounts
            .Where(row => row.AccountId == accountId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LocalRevision, row => row.LocalRevision + 1), cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("The sync account is not enrolled.");
        }

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO SyncPending (AccountId, ChatId, Kind, Key, LocalRevision, BaseRevision)
             SELECT {accountId}, {chatId}, {kind.ToStorageKey()}, {key}, LocalRevision, {baseRevision}
             FROM SyncAccounts WHERE AccountId = {accountId}
             ON CONFLICT (AccountId, ChatId, Kind, Key) DO UPDATE SET LocalRevision = excluded.LocalRevision
             """,
            cancellationToken);
    }

    /// <summary>
    /// Persists a discovery page and its cursor atomically. Receiving a head is not the
    /// same as installing its data.
    /// </summary>
    public async Task RecordDiscoveryAsync(
        string accountId,
        long expectedCursor,
        ChatSyncDiscovery page,
        CancellationToken cancellationToken = default)
    {
        var cursor = expectedCursor;
        foreach (var head in page.Heads)
        {
            if (head.Revision <= cursor || head.Revision > ChatSyncProtocol.MaximumCounter)
            {
                throw new InvalidDataException("Discovery heads must have increasing revisions.");
            }
            cursor = head.Revision;
        }

        if (page.Cursor != cursor)
        {
            throw new InvalidDataException("Discovery cannot advance beyond its returned heads.");
        }

        foreach (var head in page.Heads)
        {
            var chatId = Guid.ParseExact(head.ChatId, "D");
            var bytes = MessagePackSerializer.Serialize(ChatSyncHead.Initial(chatId), cancellationToken: cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO SyncChats (AccountId, ChatId, DiscoveredRevision, AppliedRevision, IsContentUnavailable, Head)
                 VALUES ({accountId}, {chatId}, {head.Revision}, 0, 0, {bytes})
                 ON CONFLICT (AccountId, ChatId) DO UPDATE
                 SET DiscoveredRevision = excluded.DiscoveredRevision
                 WHERE excluded.DiscoveredRevision > SyncChats.DiscoveredRevision
                 """,
                cancellationToken);
        }

        var affected = await db.SyncAccounts
            .Where(row => row.AccountId == accountId && row.ScanCursor == expectedCursor)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ScanCursor, cursor), cancellationToken);
        if (affected != 1)
        {
            throw new InvalidOperationException("The discovery baseline changed or the account is not enrolled.");
        }
    }

    /// <summary>
    /// Stores confirmed record revisions without generating upload intents.
    /// The caller installs current values in Chats or Nodes in the same transaction.
    /// </summary>
    public async Task RecordRemoteAsync(ChatSyncRemoteEntity record, CancellationToken cancellationToken = default)
    {
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO SyncRemote (AccountId, ChatId, Kind, Key, Revision)
             VALUES ({record.AccountId}, {record.ChatId}, {record.Kind.ToStorageKey()}, {record.Key}, {record.Revision})
             ON CONFLICT (AccountId, ChatId, Kind, Key) DO UPDATE SET Revision = excluded.Revision
             WHERE excluded.Revision > SyncRemote.Revision
             """,
            cancellationToken);
    }

    /// <summary>
    /// Starts a frozen submission with its exact captured revisions. The caller writes ordered
    /// parts incrementally in the same transaction. Neither operation flushes tracked chat changes.
    /// </summary>
    public async Task CreateSubmissionAsync(
        string accountId,
        ChatSyncUploadHeader header,
        IReadOnlyList<ChatSyncCapturedChange> changes,
        CancellationToken cancellationToken = default)
    {
        var chatId = Guid.ParseExact(header.ChatId, "D");
        var commitId = Guid.ParseExact(header.CommitId, "D");
        var headerBytes = MessagePackSerializer.Serialize(header, cancellationToken: cancellationToken);
        var capturedBytes = MessagePackSerializer.Serialize(changes.ToArray(), cancellationToken: cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO SyncSubmissions (AccountId, ChatId, CommitId, Header, CapturedChanges)
             VALUES ({accountId}, {chatId}, {commitId}, {headerBytes}, {capturedBytes})
             """,
            cancellationToken);
    }

    /// <summary>
    /// Appends one captured wire part at its position in the frozen submission.
    /// </summary>
    public Task WritePartAsync(string accountId, Guid chatId, int position, byte[] bytes, CancellationToken cancellationToken = default)
    {
        return db.Database.ExecuteSqlAsync(
            $"INSERT INTO SyncParts (AccountId, ChatId, Position, Body) VALUES ({accountId}, {chatId}, {position}, {bytes})",
            cancellationToken);
    }

    /// <summary>
    /// Acknowledges only this frozen submission. Edits saved during its network request
    /// remain pending, rebased onto this device's now-confirmed write.
    /// </summary>
    public async Task AcknowledgeAsync(string accountId, ChatSyncCommitResult result, CancellationToken cancellationToken = default)
    {
        var commitId = Guid.ParseExact(result.CommitId, "D");
        var submission = await db.SyncSubmissions
            .AsNoTracking()
            .SingleAsync(row => row.AccountId == accountId && row.CommitId == commitId, cancellationToken);
        var header = MessagePackSerializer.Deserialize<ChatSyncUploadHeader>(submission.Header, cancellationToken: cancellationToken);
        if (result.Revision <= header.ExpectedRevision || result.Revision > ChatSyncProtocol.MaximumCounter)
        {
            throw new InvalidDataException("The commit receipt has an invalid revision.");
        }

        var changes = MessagePackSerializer.Deserialize<ChatSyncCapturedChange[]>(submission.CapturedChanges, cancellationToken: cancellationToken);
        var lifecycle = changes.SingleOrDefault(change => change.Kind == ChatSyncRecordKind.Lifecycle);
        var laterLifecycle = db.SyncPending.Where(row => row.AccountId == accountId && row.ChatId == submission.ChatId &&
            row.Kind == ChatSyncRecordKind.Lifecycle && row.LocalRevision > (lifecycle == null ? 0 : lifecycle.LocalRevision) &&
            row.BaseRevision == header.ExpectedLifecycleRevision);
        if ((lifecycle is not null || header.ExpectedRevision == 0) && await laterLifecycle.AnyAsync(cancellationToken))
        {
            var revision = result.Revision;
            if (header.ExpectedRevision != 0 && header.Action != ChatSyncAction.Republish)
            {
                var state = await db.SyncChats
                    .AsNoTracking()
                    .SingleAsync(row => row.AccountId == accountId && row.ChatId == submission.ChatId, cancellationToken);
                var head = state.ReadAppliedHead();
                var target = header.Action switch
                {
                    ChatSyncAction.Archive => ChatSyncLifecycle.Archived,
                    ChatSyncAction.Restore => ChatSyncLifecycle.Active,
                    ChatSyncAction.Delete => ChatSyncLifecycle.Deleted,
                    _ => ChatSyncLifecycle.Active,
                };
                if (head is not null && head.Revision <= header.ExpectedRevision && head.Lifecycle == target)
                {
                    revision = header.ExpectedLifecycleRevision;
                }
                else if (head is not null && head.Revision > header.ExpectedRevision && head.LifecycleRevision <= result.Revision)
                {
                    revision = head.LifecycleRevision;
                }
            }
            // A later decision on this device follows its own confirmed transition. It must not
            // be mistaken for a stale restore of a lifecycle changed independently elsewhere.
            await laterLifecycle.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.BaseRevision, revision), cancellationToken);
        }

        foreach (var change in changes)
        {
            var pending = db.SyncPending
                .Where(row => row.AccountId == accountId && row.ChatId == submission.ChatId && row.Kind == change.Kind && row.Key == change.Key);
            await pending
                .Where(row => row.LocalRevision == change.LocalRevision)
                .ExecuteDeleteAsync(cancellationToken);
            await pending
                .Where(row => row.LocalRevision > change.LocalRevision)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        row => row.BaseRevision,
                        row => Math.Max(row.BaseRevision, result.Revision)),
                    cancellationToken);
            await RecordRemoteAsync(
                new ChatSyncRemoteEntity
                {
                    AccountId = accountId,
                    ChatId = submission.ChatId,
                    Kind = change.Kind,
                    Key = change.Key,
                    Revision = result.Revision,
                },
                cancellationToken);
        }
        await db.SyncSubmissions
            .Where(row => row.AccountId == accountId && row.ChatId == submission.ChatId)
            .ExecuteDeleteAsync(cancellationToken);
    }
}

/// <summary>
/// Exact intent revisions captured by one persisted submission, not a second mutable dirty tracker.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncCapturedChange(
    [property: Key(0)] ChatSyncRecordKind Kind,
    [property: Key(1)] string Key,
    [property: Key(2)] long LocalRevision
);
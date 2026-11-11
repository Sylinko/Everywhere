using System.Globalization;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task<ReceivedChatCommit?> MergeSnapshotAsync(
        string accountId,
        ChatSyncDownloadedSnapshot download,
        IReadOnlySet<Guid> loadedNodes,
        bool shouldDiscardSubmission,
        CancellationToken cancellationToken)
    {
        var head = download.Head;
        var chatId = Guid.ParseExact(head.ChatId, "D");
        var messages = new Dictionary<Guid, ChatMessage>();
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var store = new ChatSyncStateStore(db);
            var chat = await db.Chats.SingleOrDefaultAsync(row => row.Id == chatId, cancellationToken);
            if (chat is { IsTemporary: true })
            {
                throw new ChatSyncException(ChatSyncFailureCode.TemporaryChat);
            }
            if (chat is not null && chat.LocalAccountId != accountId)
            {
                throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
            }

            if (head.Availability != ChatSyncAvailability.Available && head.Lifecycle != ChatSyncLifecycle.Deleted)
            {
                // Stopping participation ends account-wide lifecycle writes as well as content uploads.
                // An offline archive or delete already took effect locally; never replay it on another device.
                await db.SyncPending
                    .Where(row =>
                        row.AccountId == accountId && row.ChatId == chatId &&
                        row.Kind == ChatSyncRecordKind.Lifecycle && (row.Key != "republish" || row.BaseRevision != head.Revision))
                    .ExecuteDeleteAsync(cancellationToken);
                await SaveHeadAsync(db, accountId, chatId, head, shouldDiscardSubmission, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return null;
            }

            var isRebuilt = download.CrossesRepublish;
            if (isRebuilt)
            {
                await db.SyncSubmissions
                    .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.QuotaRejection != null)
                    .ExecuteDeleteAsync(cancellationToken);
                // A decision about the preceding copy cannot delete, restore or replace its successor.
                await db.SyncPending
                    .Where(row =>
                        row.AccountId == accountId && row.ChatId == chatId &&
                        row.Kind == ChatSyncRecordKind.Lifecycle && row.BaseRevision < head.LastRepublishedRevision)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            var lifecycle = await db.SyncPending.AsNoTracking().SingleOrDefaultAsync(
                row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == ChatSyncRecordKind.Lifecycle,
                cancellationToken);
            var localAction = lifecycle is null ? ChatSyncAction.Update : ChatSyncStorageKeys.ReadAction(lifecycle.Key);
            if (head.Lifecycle == ChatSyncLifecycle.Deleted || localAction == ChatSyncAction.Delete)
            {
                // Tombstones and local deletion intent must never materialize remote content again.
                await db.Chats.Where(row => row.Id == chatId).ExecuteDeleteAsync(cancellationToken);
                if (head.Lifecycle == ChatSyncLifecycle.Deleted)
                {
                    await db.SyncPending.Where(row => row.AccountId == accountId && row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
                    await db.SyncRemote.Where(row => row.AccountId == accountId && row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
                }

                await SaveHeadAsync(
                    db,
                    accountId,
                    chatId,
                    head,
                    shouldDiscardSubmission || head.Lifecycle == ChatSyncLifecycle.Deleted,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ReceivedChatCommit(null, [], []);
            }

            var isFirstMatch = chat is not null &&
                download.SinceRevision == 0 &&
                !await TrackedSyncStates(db).AnyAsync(state => state.AccountId == accountId && state.ChatId == chatId, cancellationToken);
            if (chat is null)
            {
                chat = new ChatV2Entity
                {
                    Id = chatId,
                    LocalAccountId = accountId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                db.Chats.Add(chat);
                await db.SaveChangesAsync(cancellationToken);
            }

            var fields = ChatMetadataMapper.ReadFields(chat);
            var hasStaleRestore = localAction == ChatSyncAction.Restore && lifecycle?.BaseRevision != head.LifecycleRevision;
            var isArchived =
                localAction == ChatSyncAction.Archive ||
                (lifecycle is null || hasStaleRestore) && head.Lifecycle == ChatSyncLifecycle.Archived;
            chat.ArchivedAt = isArchived ? chat.ArchivedAt ?? DateTimeOffset.UtcNow : null;

            // Only full inventories can prove that a local branch is absent from the cloud.
            var missingNodes = isRebuilt || isFirstMatch ?
                (await db.Nodes.Where(row => row.ChatContextId == chatId).Select(row => row.Id).ToArrayAsync(cancellationToken)).ToHashSet() :
                null;
            var localChoices = isFirstMatch ?
                await db.Nodes
                    .Where(row => row.ChatContextId == chatId && row.ChoiceChildId != null)
                    .Select(row => new
                    {
                        row.Id,
                        ChildId = row.ChoiceChildId.GetValueOrDefault()
                    }).ToArrayAsync(cancellationToken) :
                null;
            var missingFields = isRebuilt || isFirstMatch ? fields.Keys.ToHashSet() : null;
            foreach (var (entry, bytes, _) in download.ReadEntries(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = entry.Type == ChatSyncRecordKind.Field ? entry.Name : entry.Id;
                if (key is null || entry.Revision > head.Revision)
                {
                    throw new InvalidDataException("Invalid remote record identity or revision.");
                }

                if (entry.Type == ChatSyncRecordKind.Node)
                {
                    missingNodes?.Remove(Guid.ParseExact(key, "D"));
                }
                else if (entry.Type == ChatSyncRecordKind.Field)
                {
                    missingFields?.Remove(key);
                }

                var remote = await db.SyncRemote
                    .AsNoTracking()
                    .SingleOrDefaultAsync(
                        row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == entry.Type && row.Key == key,
                        cancellationToken);
                if (remote is not null && remote.Revision >= entry.Revision)
                {
                    continue;
                }

                var pending = await db.SyncPending.SingleOrDefaultAsync(
                    row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == entry.Type && row.Key == key,
                    cancellationToken);
                switch (entry.Type)
                {
                    case ChatSyncRecordKind.Node:
                    {
                        var id = Guid.ParseExact(key, "D");
                        if (!Enum.IsDefined((ChatMessageKind)entry.Kind) || entry.CreatedAt is null)
                        {
                            throw new InvalidDataException("Unsupported remote message kind or creation time.");
                        }

                        var parentId = ParseId(entry.ParentId);
                        var turnId = ParseId(entry.TurnId);
                        var createdAt = ParseDate(entry.CreatedAt);
                        var summaryId = ParseId(entry.SummaryThroughNodeId);
                        var existing = await db.Nodes.AsNoTracking().SingleOrDefaultAsync(
                            row => row.ChatContextId == chatId && row.Id == id,
                            cancellationToken);
                        if (existing is not null &&
                            (existing.ParentId != parentId ||
                                existing.TurnId != turnId ||
                                existing.MessageKind != (ChatMessageKind)entry.Kind ||
                                existing.CreatedAt != createdAt))
                        {
                            throw new ChatSyncException(ChatSyncFailureCode.PayloadConflict);
                        }

                        var isSame = existing is not null &&
                            existing.SummaryThroughNodeId == summaryId &&
                            existing.Payload.AsSpan().SequenceEqual(bytes);
                        if (existing is not null && !isSame && (remote is null || isRebuilt && remote.Revision < head.LastRepublishedRevision))
                        {
                            // Without a shared baseline neither copy can establish which body is newer.
                            throw new ChatSyncException(new ChatSyncFailure(ChatSyncFailureCode.PayloadConflict, new() { ["nodeId"] = key }));
                        }

                        if (pending is not null && entry.Revision > pending.BaseRevision)
                        {
                            if (!isSame)
                            {
                                throw new ChatSyncException(ChatSyncFailureCode.PayloadConflict);
                            }

                            // Exact already-published content reconciles an expired receipt without
                            // choosing a winner or discarding any later, different local payload.
                            db.SyncPending.Remove(pending);
                        }

                        if (!isSame && pending is null)
                        {
                            await db.Database.ExecuteSqlInterpolatedAsync(
                                $"""
                                 INSERT INTO Nodes (ChatContextId, Id, ParentId, TurnId, ChoiceChildId, MessageKind,
                                     SummaryThroughNodeId, Payload, CreatedAt, UpdatedAt)
                                 VALUES ({chatId}, {id}, {parentId}, {turnId}, NULL, {entry.Kind}, {summaryId},
                                     {bytes}, {createdAt.UtcTicks}, {createdAt.UtcTicks})
                                 ON CONFLICT (ChatContextId, Id) DO UPDATE SET
                                     Payload = excluded.Payload, SummaryThroughNodeId = excluded.SummaryThroughNodeId
                                 """,
                                cancellationToken);
                            if (loadedNodes.Contains(id))
                            {
                                var message = MessagePackSerializer.Deserialize<ChatMessage>(bytes, cancellationToken: cancellationToken);
                                if (message.Kind != (ChatMessageKind)entry.Kind)
                                {
                                    (message as IDisposable)?.Dispose();
                                    throw new InvalidDataException("Remote payload kind disagrees with its topology.");
                                }
                                messages.Add(id, message);
                            }
                        }
                        break;
                    }
                    case ChatSyncRecordKind.Choice:
                    {
                        var id = Guid.ParseExact(key, "D");
                        var childId = ParseId(entry.ChildId);
                        if (isRebuilt && pending is not null && await db.Nodes.AnyAsync(
                                row => row.ChatContextId == chatId && row.Id == id && row.ChoiceChildId == childId,
                                cancellationToken))
                        {
                            db.SyncPending.Remove(pending);
                        }
                        else if (pending is null)
                        {
                            await db.Nodes
                                .Where(row => row.ChatContextId == chatId && row.Id == id)
                                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChoiceChildId, childId), cancellationToken);
                        }
                        break;
                    }
                    case ChatSyncRecordKind.Field:
                    {
                        if (isRebuilt && pending is not null &&
                            fields.GetValueOrDefault(key).AsSpan().SequenceEqual(bytes))
                        {
                            db.SyncPending.Remove(pending);
                        }
                        else if (pending is null)
                        {
                            fields[key] = bytes;
                        }
                        break;
                    }
                    default:
                    {
                        throw new InvalidDataException($"Unsupported snapshot entry {entry.Type}.");
                    }
                }

                if (pending is not null && entry.Type != ChatSyncRecordKind.Node)
                {
                    pending.BaseRevision = entry.Revision;
                }

                await store.RecordRemoteAsync(
                    new ChatSyncRemoteEntity
                    {
                        AccountId = accountId, ChatId = chatId, Kind = entry.Type, Key = key,
                        Revision = entry.Revision,
                    },
                    cancellationToken);
            }

            if (missingNodes is not null)
            {
                // Retain an offline selection only when its child was not in this first cloud copy.
                // Choices between shared children follow the cloud; no timestamps or guesses.
                if (localChoices is not null)
                {
                    foreach (var choice in localChoices)
                    {
                        if (missingNodes.Contains(choice.Id) || !missingNodes.Contains(choice.ChildId))
                        {
                            continue;
                        }
                        await db.Nodes
                            .Where(row => row.ChatContextId == chatId && row.Id == choice.Id)
                            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChoiceChildId, choice.ChildId), cancellationToken);
                        var key = choice.Id.ToString("D");
                        var baseline = await db.SyncRemote
                            .Where(row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == ChatSyncRecordKind.Choice &&
                                row.Key == key)
                            .Select(row => (long?)row.Revision).SingleOrDefaultAsync(cancellationToken) ?? 0;
                        await store.RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Choice, key, baseline, cancellationToken);
                    }
                }
                foreach (var id in missingNodes)
                {
                    var key = id.ToString("D");
                    foreach (var kind in new[] { ChatSyncRecordKind.Node, ChatSyncRecordKind.Choice })
                    {
                        if (isRebuilt)
                        {
                            await db.SyncRemote.Where(row => row.AccountId == accountId && row.ChatId == chatId &&
                                row.Kind == kind && row.Key == key).ExecuteDeleteAsync(cancellationToken);
                            await db.SyncPending.Where(row => row.AccountId == accountId && row.ChatId == chatId &&
                                row.Kind == kind && row.Key == key).ExecuteDeleteAsync(cancellationToken);
                        }
                        await store.RecordChangeAsync(accountId, chatId, kind, key, 0, cancellationToken);
                    }
                }
            }
            if (missingFields is not null)
            {
                foreach (var key in missingFields)
                {
                    // Keep opaque bytes for the writer. An absent field is new to this copy.
                    await store.RecordChangeAsync(accountId, chatId, ChatSyncRecordKind.Field, key, 0, cancellationToken);
                }
            }
            ChatMetadataMapper.ApplyFields(chat, fields);
            await db.SaveChangesAsync(cancellationToken);
            var nodes = await ReadTopologyAsync(db, chatId, cancellationToken);
            ValidateTopology(nodes);
            await SaveHeadAsync(db, accountId, chatId, head, shouldDiscardSubmission, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ReceivedChatCommit(chat, nodes, messages);
        }
        catch
        {
            foreach (var message in messages.Values.OfType<IDisposable>())
            {
                message.Dispose();
            }
            throw;
        }
    }

    private static async Task SaveHeadAsync(
        ChatV2DbContext db,
        string accountId,
        Guid chatId,
        ChatSyncHead head,
        bool shouldDiscardSubmission,
        CancellationToken cancellationToken)
    {
        var isUnavailable = head.Availability != ChatSyncAvailability.Available;
        var bytes = MessagePackSerializer.Serialize(head, cancellationToken: cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO SyncChats (AccountId, ChatId, DiscoveredRevision, AppliedRevision, IsContentUnavailable, Head)
             VALUES ({accountId}, {chatId}, {head.Revision}, {head.Revision}, {isUnavailable}, {bytes})
             ON CONFLICT (AccountId, ChatId) DO UPDATE SET AppliedRevision = {head.Revision},
                 IsContentUnavailable = excluded.IsContentUnavailable,
                 Head = excluded.Head,
                 DiscoveredRevision = MAX(DiscoveredRevision, {head.Revision})
             """,
            cancellationToken);
        if (!isUnavailable || head.Lifecycle == ChatSyncLifecycle.Deleted)
        {
            await new ChatSyncStateStore(db).RecordRemoteAsync(
                new ChatSyncRemoteEntity
                {
                    AccountId = accountId, ChatId = chatId, Kind = ChatSyncRecordKind.Lifecycle, Key = "state", Revision = head.LifecycleRevision,
                },
                cancellationToken);
        }

        var submissions = db.SyncSubmissions.Where(row => row.AccountId == accountId && row.ChatId == chatId);
        if (shouldDiscardSubmission)
        {
            await submissions.ExecuteDeleteAsync(cancellationToken);
        }
        else if (isUnavailable && !await db.SyncPending.AnyAsync(
                     row => row.AccountId == accountId && row.ChatId == chatId && row.Kind == ChatSyncRecordKind.Lifecycle && row.Key == "republish",
                     cancellationToken))
        {
            await submissions.Where(row => row.QuotaRejection != null).ExecuteDeleteAsync(cancellationToken);
        }
    }

    private static void ValidateTopology(ChatNodeTopology[] nodes)
    {
        var map = nodes.AsValueEnumerable().ToDictionary(row => row.Id, row => row);
        var roots = nodes.AsValueEnumerable().Where(row => row.ParentId is null).ToArray();
        if (roots.Length != 1 || roots[0].MessageKind != ChatMessageKind.Root)
        {
            throw new InvalidDataException("A remote chat must have one root.");
        }

        var children = nodes.AsValueEnumerable().Where(row => row.ParentId is not null).ToLookup(row => row.ParentId);
        var queue = new Queue<ChatNodeTopology>();
        queue.Enqueue(roots[0]);
        var count = 0;
        while (queue.TryDequeue(out var parent))
        {
            count++;
            if (parent.ChoiceChildId is { } choice && (!map.TryGetValue(choice, out var chosen) || chosen.ParentId != parent.Id))
            {
                throw new InvalidDataException("A choice must identify a direct child.");
            }

            foreach (var child in children[parent.Id])
            {
                if (child.TurnId != (child.MessageKind.IsTurnStart() ? child.Id : parent.TurnId))
                {
                    throw new InvalidDataException("Turn membership disagrees with parentage.");
                }

                queue.Enqueue(child);
            }
        }
        if (count != nodes.Length)
        {
            throw new InvalidDataException("The remote graph is disconnected.");
        }
    }

    private static Guid? ParseId(string? value) => value is null ? null : Guid.ParseExact(value, "D");

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>
/// Committed metadata and graph values for publication into an existing runtime. Only previously
/// loaded bodies have replacements. A null metadata row denotes permanent deletion.
/// </summary>
public sealed record ReceivedChatCommit(
    ChatV2Entity? Metadata,
    ChatNodeTopology[] Nodes,
    Dictionary<Guid, ChatMessage> Messages
);
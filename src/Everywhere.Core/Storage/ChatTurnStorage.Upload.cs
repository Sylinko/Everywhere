using System.Globalization;
using Everywhere.Cloud;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <summary>
    /// Captures one immutable request. Existing uncertain submissions are never regenerated.
    /// While work is running, only independently edited topic and pin fields are eligible.
    /// </summary>
    public async Task FreezeUploadAsync(string accountId, Guid chatId, bool isStable, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.SyncSubmissions.AnyAsync(row => row.AccountId == accountId && row.ChatId == chatId, cancellationToken))
        {
            return;
        }

        var chat = await db.Chats.AsNoTracking().SingleOrDefaultAsync(row => row.Id == chatId, cancellationToken);
        if (chat?.IsTemporary == true)
        {
            return;
        }

        var state = await db.SyncChats.AsNoTracking().SingleAsync(row => row.AccountId == accountId && row.ChatId == chatId, cancellationToken);
        var head = state.ReadAppliedHead();
        if (head is null || head.Lifecycle == ChatSyncLifecycle.Deleted || !isStable && head.Revision == 0)
        {
            return;
        }

        var independentKeys = ChatMetadataMapper.IndependentKeys;
        var pending = await db.SyncPending.AsNoTracking()
            .Where(row => row.AccountId == accountId && row.ChatId == chatId &&
                (isStable || row.Kind == ChatSyncRecordKind.Field && independentKeys.Contains(row.Key)))
            .ToArrayAsync(cancellationToken);
        if (pending.Length == 0)
        {
            return;
        }

        var lifecycle = pending.SingleOrDefault(row => row.Kind == ChatSyncRecordKind.Lifecycle);
        var action = lifecycle is null ? ChatSyncAction.Update : ChatSyncStorageKeys.ReadAction(lifecycle.Key);
        var isRepublish = action == ChatSyncAction.Republish;
        if (isRepublish)
        {
            if (!state.IsContentUnavailable || chat is null || lifecycle?.BaseRevision != head.Revision)
            {
                throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
            }
        }
        else if (state.IsContentUnavailable)
        {
            return;
        }
        else if (action == ChatSyncAction.Restore && lifecycle?.BaseRevision != head.LifecycleRevision)
        {
            throw new ChatSyncException(ChatSyncFailureCode.LifecycleConflict);
        }
        else if (action == ChatSyncAction.Delete || head.Revision > 0 && lifecycle is not null)
        {
            // Publish lifecycle separately from growth, so quota cannot prevent archive/delete.
            pending = [lifecycle ?? throw new InvalidDataException("Missing deletion intent.")];
        }
        else if (chat is null)
        {
            return;
        }

        var header = new ChatSyncUploadHeader(
            Guid.NewGuid().ToString("D"),
            chatId.ToString("D"),
            head.Revision,
            isRepublish ? 2 : 1,
            action,
            head.LifecycleRevision,
            isRepublish && chat?.ArchivedAt is not null);
        var fields = chat is null ? new Dictionary<string, byte[]>() : ChatMetadataMapper.ReadFields(chat);
        var edits = pending.ToDictionary(row => (row.Kind, row.Key));
        var changes = new List<ChatSyncCapturedChange>();
        // Persist parts as they are captured; a whole history's payloads need not coexist in memory.
        var store = new ChatSyncStateStore(db);
        var position = 0;
        // A republish includes records that no longer have pending edits. Zero is not
        // a local edit revision: later edits to those records still need acknowledgement baselines.
        if (isRepublish)
        {
            Add(ChatSyncRecordKind.Lifecycle, "republish");
            var ids = await db.Nodes.Where(row => row.ChatContextId == chatId).Select(row => row.Id).ToArrayAsync(cancellationToken);
            foreach (var id in ids)
            {
                Add(ChatSyncRecordKind.Node, id.ToString("D"));
                Add(ChatSyncRecordKind.Choice, id.ToString("D"));
            }

            foreach (var key in fields.Keys)
            {
                Add(ChatSyncRecordKind.Field, key);
            }

            void Add(ChatSyncRecordKind kind, string key)
            {
                changes.Add(new ChatSyncCapturedChange(kind, key, edits.GetValueOrDefault((kind, key))?.LocalRevision ?? 0));
            }
        }
        else
        {
            changes.AddRange(pending.Select(row => new ChatSyncCapturedChange(row.Kind, row.Key, row.LocalRevision)));
        }

        await store.CreateSubmissionAsync(accountId, header, changes, cancellationToken);
        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case ChatSyncRecordKind.Node:
                {
                    var id = Guid.ParseExact(change.Key, "D");
                    var node = await db.Nodes
                        .AsNoTracking()
                        .SingleAsync(row => row.ChatContextId == chatId && row.Id == id, cancellationToken);
                    var nodeHeader = new ChatSyncNodeHeader(
                        change.Key,
                        node.ParentId?.ToString("D"),
                        node.TurnId?.ToString("D"),
                        (int)node.MessageKind,
                        FormatDate(node.CreatedAt),
                        node.SummaryThroughNodeId?.ToString("D"),
                        isRepublish ? 0 : edits[(change.Kind, change.Key)].BaseRevision);
                    for (var offset = 0; offset < node.Payload.Length; offset += 512 * 1024)
                    {
                        var count = Math.Min(512 * 1024, node.Payload.Length - offset);
                        await WriteAsync(
                            new ChatSyncUploadPart
                            {
                                Type = ChatSyncRecordKind.Node,
                                Node = nodeHeader,
                                Offset = offset,
                                Body = [.. node.Payload.AsSpan(offset, count)],
                                IsComplete = offset + count == node.Payload.Length,
                            });
                    }
                    break;
                }
                case ChatSyncRecordKind.Choice:
                {
                    var id = Guid.ParseExact(change.Key, "D");
                    var child = await db.Nodes
                        .Where(row => row.ChatContextId == chatId && row.Id == id)
                        .Select(row => row.ChoiceChildId)
                        .SingleAsync(cancellationToken);
                    await WriteAsync(new ChatSyncUploadPart { Type = ChatSyncRecordKind.Choice, Id = change.Key, ChildId = child?.ToString("D") });
                    break;
                }
                case ChatSyncRecordKind.Field:
                {
                    if (chat is null)
                    {
                        throw new InvalidDataException("A field intent has no local conversation.");
                    }
                    var body = fields[change.Key];
                    await WriteAsync(new ChatSyncUploadPart { Type = ChatSyncRecordKind.Field, Name = change.Key, Body = body });
                    break;
                }
                case ChatSyncRecordKind.Lifecycle:
                    break;
                default:
                    throw new InvalidDataException($"Unsupported pending intent {change.Kind}.");
            }
        }
        await transaction.CommitAsync(cancellationToken);

        async Task WriteAsync(ChatSyncUploadPart part)
        {
            var bytes = MessagePackSerializer.Serialize(part, cancellationToken: cancellationToken);
            await store.WritePartAsync(accountId, chatId, position++, bytes, cancellationToken);
        }
    }

    private static string FormatDate(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
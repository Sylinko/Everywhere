using Everywhere.Cloud;
using Everywhere.Chat;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <summary>
    /// Saves only pending node revisions. Choice-only writes never read or serialize payloads.
    /// The manager acknowledges captured revisions after this transaction commits.
    /// </summary>
    public async Task SaveChatContextAsync(ChatWriteBatch batch, CancellationToken cancellationToken = default)
    {
        var chatId = batch.Metadata.Snapshot.Id;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var localAccountId = batch.Metadata.Snapshot.IsTemporary ? null : batch.LocalAccountId ?? SyncAccountId;
        var previous = await db.Chats.AsNoTracking().SingleOrDefaultAsync(row => row.Id == chatId, cancellationToken);
        if (previous is null)
        {
            var chat = batch.Metadata.Snapshot;
            chat.LocalAccountId = localAccountId;
            db.Chats.Add(chat);
            await db.SaveChangesAsync(cancellationToken);
        }
        else
        {
            await UpdateMetadataAsync(db, batch.Metadata, cancellationToken);
        }

        if (previous is { IsTemporary: true } && !batch.Metadata.Snapshot.IsTemporary)
        {
            await db.Chats
                .Where(chat => chat.Id == chatId)
                .ExecuteUpdateAsync(update => update.SetProperty(chat => chat.LocalAccountId, localAccountId), cancellationToken);
        }

        foreach (var write in batch.Nodes)
        {
            await WriteNodeAsync(db, chatId, write, cancellationToken);
        }

        // First-save handoff shares the graph transaction:
        //   Chats + Nodes -> remaining input under the real ID -> remove the owned new-chat slot.
        // Failure leaves the preceding slot intact. Delayed input writes choose their destination
        // from the committed Chats row, so they cannot recreate that slot after handoff.
        if (batch.LocalInput is { } input)
        {
            await WriteInputAsync(db, chatId, input, cancellationToken);
        }
        else if (previous is null)
        {
            if (batch.Metadata.Snapshot.IsTemporary)
            {
                await db.ChatInputs.Where(row => row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
            }
            else
            {
                await db.ChatInputs
                    .Where(row => row.SlotId == Guid.Empty && row.ChatId == chatId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SlotId, chatId), cancellationToken);
            }
        }

        await RecordSyncChangesAsync(db, batch, previous, localAccountId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Archives a conversation without canceling running work or changing any node.
    /// Subsequent saves preserve ArchivedAt.
    /// </summary>
    public async Task<DateTimeOffset> ArchiveAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        var archivedAt = DateTimeOffset.UtcNow;
        await SetArchiveStateAsync(chatId, archivedAt, cancellationToken);
        return archivedAt;
    }

    /// <summary>
    /// Restores a conversation to the active listing without rewriting its history.
    /// </summary>
    public Task RestoreAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        return SetArchiveStateAsync(chatId, null, cancellationToken);
    }

    /// <summary>
    /// Removes a conversation after the manager has stopped its work. Pending saves are discarded after deletion succeeds.
    /// Attachment files and shared blob metadata are retained for legacy and other chat references.
    /// </summary>
    public async Task DeletePermanentlyAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RecordSyncLifecycleAsync(db, chatId, ChatSyncAction.Delete, cancellationToken);
        await db.ChatInputs.Where(row => row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
        await db.Chats.Where(row => row.Id == chatId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetArchiveStateAsync(Guid chatId, DateTimeOffset? archivedAt, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var affected = await db.Chats
            .Where(row => row.Id == chatId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ArchivedAt, archivedAt), cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Conversation {chatId} does not exist.");
        }

        await RecordSyncLifecycleAsync(db, chatId, archivedAt is null ? ChatSyncAction.Restore : ChatSyncAction.Archive, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task WriteNodeAsync(ChatV2DbContext db, Guid chatId, ChatNodeWrite write, CancellationToken cancellationToken)
    {
        var node = write.Topology;
        if (write.Payload is null)
        {
            await WriteChoiceAsync(db, chatId, write, cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Nodes (ChatContextId, Id, ParentId, TurnId, ChoiceChildId,
                 MessageKind, SummaryThroughNodeId, Payload, CreatedAt, UpdatedAt)
             VALUES ({chatId}, {node.Id}, {node.ParentId}, {node.TurnId}, NULL,
                 {(int)node.MessageKind}, {node.SummaryThroughNodeId}, {write.Payload}, {node.CreatedAt.UtcTicks}, {node.UpdatedAt.UtcTicks})
             ON CONFLICT (ChatContextId, Id) DO UPDATE SET
                 SummaryThroughNodeId = excluded.SummaryThroughNodeId, Payload = excluded.Payload, UpdatedAt = excluded.UpdatedAt
             """,
            cancellationToken);

        await WriteChoiceAsync(db, chatId, write, cancellationToken);

        await db.Database.ExecuteSqlAsync(
            $"DELETE FROM NodeBlobs WHERE ChatContextId = {chatId} AND ChatNodeId = {node.Id}",
            cancellationToken);
        foreach (var file in write.Files)
        {
            await WriteBlobAsync(db, file, node.UpdatedAt, cancellationToken);

            await db.Database.ExecuteSqlAsync(
                $"INSERT INTO NodeBlobs (ChatContextId, ChatNodeId, BlobSha256) VALUES ({chatId}, {node.Id}, {file.Sha256})",
                cancellationToken);
        }
    }

    private static async Task WriteBlobAsync(ChatV2DbContext db, ChatBlobWrite file, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        if (ReadFileSize(file.LocalPath) is not { } size)
        {
            return;
        }
        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO Blobs (Sha256, LocalPath, MimeType, Size, CreatedAt, LastAccessAt)
             VALUES ({file.Sha256}, {file.LocalPath}, {file.MimeType}, {size}, {updatedAt.UtcTicks}, {updatedAt.UtcTicks})
             ON CONFLICT (Sha256) DO UPDATE SET LastAccessAt = excluded.LastAccessAt
             """,
            cancellationToken);
    }

    private static async Task WriteChoiceAsync(ChatV2DbContext db, Guid chatId, ChatNodeWrite write, CancellationToken cancellationToken)
    {
        if ((write.Kind & ChatNodeChangeKind.Choice) == 0)
        {
            return;
        }

        var node = write.Topology;
        await db.Database.ExecuteSqlAsync(
            $"""
             UPDATE Nodes SET ChoiceChildId = {node.ChoiceChildId}, UpdatedAt = {node.UpdatedAt.UtcTicks}
             WHERE ChatContextId = {chatId} AND Id = {node.Id}
             """,
            cancellationToken);
    }

    private static long? ReadFileSize(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch (IOException)
        {
            // A missing local file does not erase its durable reference.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
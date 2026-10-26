using Everywhere.Chat;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <summary>
    /// Saves only pending node revisions. Relationship-only writes never read or serialize payloads.
    /// The manager acknowledges captured revisions after this transaction commits.
    /// </summary>
    public async Task SaveChatContextAsync(ChatWriteBatch batch, CancellationToken cancellationToken = default)
    {
        var chatId = batch.Metadata.Id;
        var topic = batch.Metadata.Topic;
        var createdAt = batch.Metadata.DateCreated.UtcTicks;
        var updatedAt = batch.Metadata.DateModified.UtcTicks;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO Chats (Id, Topic, CreatedAt, UpdatedAt) VALUES ({chatId}, {topic}, {createdAt}, {updatedAt})
            ON CONFLICT (Id) DO UPDATE SET Topic = excluded.Topic, UpdatedAt = excluded.UpdatedAt
            """,
            cancellationToken);

        foreach (var write in batch.Nodes)
        {
            await WriteNodeAsync(db, chatId, write, cancellationToken);
        }

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
    /// Removes an archived conversation after the manager has stopped its work and discarded pending saves.
    /// Attachment files and shared blob metadata are retained for legacy and other chat references.
    /// </summary>
    public async Task DeletePermanentlyAsync(Guid chatId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var chat = await db.Chats.AsNoTracking().SingleAsync(row => row.Id == chatId, cancellationToken);
        if (chat.ArchivedAt is null)
        {
            throw new InvalidOperationException("Archive the conversation before permanently deleting it.");
        }

        await db.Chats.Where(row => row.Id == chatId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task SetArchiveStateAsync(Guid chatId, DateTimeOffset? archivedAt, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var affected = await db.Chats.Where(row => row.Id == chatId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ArchivedAt, archivedAt), cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Conversation {chatId} does not exist.");
        }
    }

    private static async Task WriteNodeAsync(ChatV2DbContext db, Guid chatId, ChatNodeWrite write, CancellationToken cancellationToken)
    {
        var node = write.Topology;
        if (write.Payload is null)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE Nodes SET ParentId = {node.ParentId}, TurnId = {node.TurnId}, SiblingOrder = {node.SiblingOrder},
                    ChoiceChildId = {node.ChoiceChildId}, UpdatedAt = {node.UpdatedAt.UtcTicks}
                WHERE ChatContextId = {chatId} AND Id = {node.Id}
                """,
                cancellationToken);
            return;
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO Nodes (ChatContextId, Id, ParentId, TurnId, SiblingOrder, ChoiceChildId,
                MessageKind, SummaryThroughNodeId, Payload, CreatedAt, UpdatedAt)
            VALUES ({chatId}, {node.Id}, {node.ParentId}, {node.TurnId}, {node.SiblingOrder}, {node.ChoiceChildId},
                {(int)node.MessageKind}, {write.SummaryThroughNodeId}, {write.Payload}, {node.CreatedAt.UtcTicks}, {node.UpdatedAt.UtcTicks})
            ON CONFLICT (ChatContextId, Id) DO UPDATE SET
                ParentId = excluded.ParentId, TurnId = excluded.TurnId, SiblingOrder = excluded.SiblingOrder,
                ChoiceChildId = excluded.ChoiceChildId, MessageKind = excluded.MessageKind,
                SummaryThroughNodeId = excluded.SummaryThroughNodeId, Payload = excluded.Payload, UpdatedAt = excluded.UpdatedAt
            """,
            cancellationToken);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM NodeBlobs WHERE ChatContextId = {chatId} AND ChatNodeId = {node.Id}", cancellationToken);
        foreach (var file in write.Files)
        {
            if (ReadFileSize(file.LocalPath) is { } size)
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO Blobs (Sha256, LocalPath, MimeType, Size, CreatedAt, LastAccessAt)
                    VALUES ({file.Sha256}, {file.LocalPath}, {file.MimeType}, {size}, {node.UpdatedAt.UtcTicks}, {node.UpdatedAt.UtcTicks})
                    ON CONFLICT (Sha256) DO UPDATE SET LastAccessAt = excluded.LastAccessAt
                    """,
                    cancellationToken);
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO NodeBlobs (ChatContextId, ChatNodeId, BlobSha256) VALUES ({chatId}, {node.Id}, {file.Sha256})",
                cancellationToken);
        }
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
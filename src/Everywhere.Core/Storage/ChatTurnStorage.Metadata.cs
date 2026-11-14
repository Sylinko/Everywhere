using Everywhere.Chat;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var affected = await db.Chats.Where(row => row.Id == chatId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.IsPinned, isPinned), cancellationToken);
        if (affected == 0)
        {
            throw new KeyNotFoundException($"Conversation {chatId} does not exist.");
        }

        await RecordSyncFieldAsync(db, chatId, ChatMetadataFields.IsPinned.Key, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task UpdateMetadataAsync(ChatV2DbContext db, ChatMetadataWrite metadata, CancellationToken cancellationToken)
    {
        if (metadata.ChangedFields.Count == 0 && !metadata.ShouldWriteRetention)
        {
            return;
        }

        var chat = await db.Chats.SingleOrDefaultAsync(row => row.Id == metadata.Snapshot.Id, cancellationToken);
        if (chat is null)
        {
            // Empty chats are inserted with their first node batch, not a title-only save.
            return;
        }

        ChatMetadataMapper.CopyFields(metadata.Snapshot, chat, metadata.ChangedFields);
        if (metadata.ShouldWriteRetention)
        {
            chat.IsTemporary = metadata.Snapshot.IsTemporary;
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}
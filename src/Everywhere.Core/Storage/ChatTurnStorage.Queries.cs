using Everywhere.Chat;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task<ChatHistoryPage> ReadHistoryPageAsync(
        int take,
        ChatHistoryCursor? cursor = null,
        CancellationToken cancellationToken = default,
        ChatHistoryView view = ChatHistoryView.Recent)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = view switch
        {
            ChatHistoryView.Archived => db.Chats.AsNoTracking().Where(row => row.ArchivedAt != null),
            ChatHistoryView.Temporary => db.Chats.AsNoTracking().Where(row => row.ArchivedAt == null && row.IsTemporary),
            _ => db.Chats.AsNoTracking().Where(row => row.ArchivedAt == null)
        };
        if (cursor is not null)
        {
            query = query.Where(row => cursor.IsPinned && !row.IsPinned ||
                row.IsPinned == cursor.IsPinned && (row.UpdatedAt < cursor.UpdatedAt ||
                    row.UpdatedAt == cursor.UpdatedAt && row.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query.OrderByDescending(row => row.IsPinned)
            .ThenByDescending(row => row.UpdatedAt).ThenByDescending(row => row.Id)
            .Take(take + 1).ToArrayAsync(cancellationToken);
        var items = rows.Take(take).Select(GetMetadata).ToArray();
        var nextCursor = rows.Length > take
            ? new ChatHistoryCursor(rows[take - 1].IsPinned, rows[take - 1].UpdatedAt, rows[take - 1].Id)
            : null;
        return new ChatHistoryPage(items, nextCursor);
    }

    /// <inheritdoc />
    public async Task<ChatMetadata?> GetMostRecentChatMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Chats.AsNoTracking().Where(row => row.ArchivedAt == null)
            .OrderByDescending(row => row.UpdatedAt).ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : GetMetadata(row);
    }

    /// <summary>
    /// Updates an existing conversation's topic without loading nodes or changing its archive state.
    /// An unsaved empty conversation is inserted only with its first actual node batch.
    /// </summary>
    public async Task SaveChatContextMetadataAsync(ChatMetadataWrite metadata, CancellationToken cancellationToken = default)
    {
        var topic = metadata.Topic;
        var updatedAt = metadata.DateModified;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Chats.Where(row => row.Id == metadata.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Topic, topic).SetProperty(row => row.UpdatedAt, updatedAt), cancellationToken);
    }

    private static ChatMetadata GetMetadata(ChatV2Entity row)
    {
        return new ChatMetadata(row.Id, row.CreatedAt, row.UpdatedAt, row.Topic)
        {
            ArchivedAt = row.ArchivedAt,
            IsPinned = row.IsPinned,
            IsTemporary = row.IsTemporary
        };
    }
}
using Everywhere.Chat;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task<ChatHistoryPage> ReadHistoryPageAsync(
        int take,
        ChatHistoryCursor? cursor = null,
        ChatHistoryView view = ChatHistoryView.Recent,
        CancellationToken cancellationToken = default)
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
                    // EF's SQLite provider translates Guid.CompareTo to the same SQL comparison used by ORDER BY.
                    // ReSharper disable once EntityFramework.UnsupportedServerSideFunctionCall
                    row.UpdatedAt == cursor.UpdatedAt && row.Id.CompareTo(cursor.Id) < 0));
        }

        var rows = await query
            .OrderByDescending(row => row.IsPinned)
            .ThenByDescending(row => row.UpdatedAt)
            .ThenByDescending(row => row.Id)
            .Take(take + 1)
            .ToArrayAsync(cancellationToken);
        var items = rows.AsValueEnumerable().Take(take).Select(ChatMetadataMapper.Materialize).ToArray();
        var nextCursor = rows.Length > take ? new ChatHistoryCursor(rows[take - 1].IsPinned, rows[take - 1].UpdatedAt, rows[take - 1].Id) : null;
        return new ChatHistoryPage(items, nextCursor);
    }

    /// <inheritdoc />
    public async Task<ChatMetadata?> GetMostRecentChatMetadataAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Chats
            .AsNoTracking()
            .Where(row => row.ArchivedAt == null)
            .OrderByDescending(row => row.UpdatedAt)
            .ThenByDescending(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ChatMetadataMapper.Materialize(row);
    }

    /// <summary>
    /// Updates explicitly changed fields without loading nodes or changing archive state.
    /// An unsaved empty conversation is inserted only with its first actual node batch.
    /// </summary>
    public async Task SaveChatContextMetadataAsync(ChatMetadataWrite metadata, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await UpdateMetadataAsync(db, metadata, cancellationToken);
        foreach (var field in metadata.ChangedFields)
        {
            await RecordSyncFieldAsync(db, metadata.Snapshot.Id, field.Key, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }
}
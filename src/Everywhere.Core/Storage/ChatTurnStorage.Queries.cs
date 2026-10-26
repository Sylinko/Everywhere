using System.Runtime.CompilerServices;
using Everywhere.Chat;
using Everywhere.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <summary>
    /// Reads a metadata page without loading topology or payloads. Active and archived listings
    /// share deterministic key/ID cursors. The manager maintains canonical metadata identity.
    /// </summary>
    public async IAsyncEnumerable<ChatMetadata> QueryChatContextsAsync(
        int take,
        Guid? startAfterId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default,
        bool isArchived = false)
    {
        ChatMetadata[] page;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
            await using var readScope = await db.Database.UseTransactionAsync(transaction, cancellationToken);
            var anchor = startAfterId is { } id ? await db.Chats.AsNoTracking().SingleOrDefaultAsync(row => row.Id == id, cancellationToken) : null;
            var query = db.Chats.AsNoTracking().Where(row => (row.ArchivedAt != null) == isArchived);
            if (anchor is not null)
            {
                query = query.Where(row => row.UpdatedAt < anchor.UpdatedAt ||
                    row.UpdatedAt == anchor.UpdatedAt && row.Id.CompareTo(anchor.Id) < 0);
            }

            var ordered = query.OrderByDescending(row => row.UpdatedAt).ThenByDescending(row => row.Id);
            var rows = await ordered.Take(take).ToArrayAsync(cancellationToken);
            page = rows.Select(GetMetadata).ToArray();
            await transaction.CommitAsync(cancellationToken);
        }

        // Never keep a SQLite reader open across the consumer's work.
        foreach (var metadata in page)
        {
            yield return metadata;
        }
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
        return new ChatMetadata(row.Id, row.CreatedAt, row.UpdatedAt, row.Topic) { ArchivedAt = row.ArchivedAt };
    }
}
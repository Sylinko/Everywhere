using Everywhere.Chat;

namespace Everywhere.Storage;

/// <summary>
/// Abstraction for chat database operations used by the application.
/// Only exposes use-case level methods instead of leaking IQueryable,
/// which keeps the implementation swappable (SQLite, Dapper, etc.).
/// </summary>
public interface IChatContextStorage : IChatTurnLoader
{
    /// <summary>
    /// Moves a conversation into the archive without modifying its nodes or stopping active work.
    /// </summary>
    /// <returns>The committed timestamp. The manager publishes it to the owning execution domain.</returns>
    Task<DateTimeOffset> ArchiveAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores an archived conversation to the active history listing.
    /// </summary>
    Task RestoreAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes a conversation after the manager stops its work. Pending writes are discarded after deletion succeeds.
    /// Shared attachment files are retained.
    /// </summary>
    Task DeletePermanentlyAsync(Guid chatId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries metadata in descending UpdatedAt/Id order with cursor-based pagination.
    /// </summary>
    /// <param name="take">Maximum number of items to return (page size).</param>
    /// <param name="startAfterId">
    /// Optional "start after" cursor. When provided, the page starts after this ID using the same sort order.
    /// Implementations should use a deterministic tie-breaker (ID) when multiple rows share the same sort key.
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <param name="isArchived">Selects the archive instead of active history.</param>
    /// <returns>Async sequence of contexts, ordered.</returns>
    IAsyncEnumerable<ChatMetadata> QueryChatContextsAsync(
        int take,
        Guid? startAfterId = null,
        CancellationToken cancellationToken = default,
        bool isArchived = false);

    /// <summary>
    /// Loads detached lightweight topology for an active or archived conversation.
    /// Message bodies are loaded explicitly by turn.
    /// </summary>
    Task<ChatData> GetChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists pending node revisions and conversation metadata in one transaction.
    /// Relationship-only changes do not load or rewrite payloads; absent bodies are not deletions.
    /// </summary>
    Task SaveChatContextAsync(ChatWriteBatch batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves chat data metadata only (e.g., topic changes).
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SaveChatContextMetadataAsync(ChatMetadataWrite metadata, CancellationToken cancellationToken = default);
}
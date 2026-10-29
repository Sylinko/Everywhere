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
    /// Reads history in descending IsPinned/UpdatedAt/Id order, after the captured database values.
    /// </summary>
    Task<ChatHistoryPage> ReadHistoryPageAsync(
        int take,
        ChatHistoryCursor? cursor = null,
        CancellationToken cancellationToken = default,
        ChatHistoryView view = ChatHistoryView.Recent);

    /// <summary>
    /// Reads the most recently modified active conversation, regardless of pinning.
    /// </summary>
    Task<ChatMetadata?> GetMostRecentChatMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits pinning without changing the conversation's modification date or message history.
    /// </summary>
    Task SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes session-only conversations after all writers have stopped. Shared attachments are retained.
    /// </summary>
    Task DeleteTemporaryChatsAsync(CancellationToken cancellationToken = default);

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
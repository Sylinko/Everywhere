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
        ChatHistoryView view = ChatHistoryView.Recent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the most recently modified active conversation, regardless of pinning.
    /// </summary>
    Task<ChatMetadata?> GetMostRecentChatMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits a history pin without loading message history or changing activity time.
    /// The caller publishes the new value only after this operation commits.
    /// </summary>
    Task SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads detached lightweight topology for an active or archived conversation.
    /// Message bodies are loaded explicitly by turn.
    /// </summary>
    Task<ChatData> GetChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists explicit node, choice, and metadata changes in one transaction.
    /// Choice-only writes do not load payloads; unchanged fields retain their stored values.
    /// </summary>
    Task SaveChatContextAsync(ChatWriteBatch batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves explicitly changed metadata fields without loading the conversation.
    /// </summary>
    Task SaveChatContextMetadataAsync(ChatMetadataWrite metadata, CancellationToken cancellationToken = default);
}
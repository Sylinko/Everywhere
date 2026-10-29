using CommunityToolkit.Mvvm.Input;
using Everywhere.Collections;

namespace Everywhere.Chat;

/// <summary>
/// Owns the current root conversation and the incrementally materialized chat-history presentation.
/// </summary>
public interface IChatContextManager : IIncrementalLoader
{
    /// <summary>
    /// Gets the current root context.
    /// </summary>
    RootChatContext Current { get; }

    /// <summary>
    /// Gets or sets the current conversation metadata. Setting this loads its root context. A binding may temporarily assign null to indicate no selection.
    /// </summary>
    ChatMetadata? CurrentMetadata { get; set; }

    /// <summary>
    /// Gets the command that invalidates cached paging state and reloads history from the newest row.
    /// </summary>
    IRelayCommand UpdateRecentHistoryCommand { get; }

    /// <summary>
    /// Gets the stable, dynamically grouped portion of history materialized so far.
    /// </summary>
    IReadOnlyBindableList<ChatDataHistoryGroup> AllHistory { get; }

    /// <summary>
    /// Gets or sets the text used to filter incrementally materialized history.
    /// </summary>
    string? HistorySearchQuery { get; set; }

    /// <summary>
    /// Gets or sets whether title misses may inspect user and assistant text content. Tool calls and
    /// tool results never participate in this search.
    /// </summary>
    bool HistorySearchIncludesContent { get; set; }

    /// <summary>
    /// Gets the number of running chat contexts in the background.
    /// </summary>
    int BackgroundBusyCount { get; }

    /// <summary>
    /// Gets the number of unacknowledged notifications from background chat contexts.
    /// </summary>
    int BackgroundNotificationCount { get; }

    /// <summary>
    /// Gets the command that creates and activates a new root conversation.
    /// </summary>
    IRelayCommand CreateNewCommand { get; }

    /// <summary>
    /// Gets or sets whether history displays archived conversations instead of active ones.
    /// </summary>
    bool IsArchiveView { get; set; }

    /// <summary>
    /// Archives a saved conversation, leaving running work on its current branch.
    /// </summary>
    Task ArchiveAsync(ChatMetadata metadata);

    /// <summary>
    /// Renames canonical metadata without loading history. An open root applies the change in its execution domain.
    /// </summary>
    Task RenameAsync(ChatMetadata metadata, string? topic);

    /// <summary>
    /// Restores a conversation to the active listing.
    /// </summary>
    Task RestoreAsync(ChatMetadata metadata);

    /// <summary>
    /// Deletes an inactive conversation after the presentation obtains confirmation.
    /// </summary>
    Task DeletePermanentlyAsync(ChatMetadata metadata);

    /// <summary>
    /// Removes a temporary conversation from the history listing without writing it to disk.
    /// </summary>
    Task DismissTemporaryAsync(ChatMetadata metadata);

    /// <summary>
    /// Loads the canonical root for the given metadata; consumers explicitly load the bodies they need.
    /// </summary>
    Task<RootChatContext?> LoadChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for earlier writes and saves the pending batch without the normal debounce delay.
    /// Failures remain queued and are reported to the caller, including application shutdown.
    /// </summary>
    Task FlushPendingSavesAsync();
}
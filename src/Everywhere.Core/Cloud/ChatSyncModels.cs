namespace Everywhere.Cloud;

/// <summary>
/// The local view and remote inventory read for this account generation.
/// </summary>
public sealed record ChatSyncInventory(string AccountId, long Generation, ChatSyncStorageUsage Usage, IReadOnlyList<ChatSyncItem> Items)
{
    /// <summary>
    /// Captures this inventory's account and generation alongside the displayed chat revisions.
    /// </summary>
    public ChatSyncDecision DecisionFor(ChatSyncItem item) => new(AccountId, Generation, item);
}

/// <summary>
/// The account, generation and chat revisions shown to the user before changing participation.
/// </summary>
public sealed record ChatSyncDecision(string AccountId, long Generation, ChatSyncItem Item);

/// <summary>
/// Confirmed stops are not rolled back if a later removal or the target upload fails.
/// </summary>
public sealed record ChatSyncReleaseResult(int StoppedCount, bool NeedsReview);

/// <summary>
/// A missing progress value denotes local-only participation. A remote-only copy may be stopped but cannot be republished.
/// </summary>
/// <param name="Id">Stable conversation identity.</param>
/// <param name="Title">User-provided local title, or the remote preview when the chat is absent locally.</param>
/// <param name="HasLocalCopy">Whether this device retains a non-temporary graph.</param>
/// <param name="Participation">Account-wide participation decision, independent of device progress.</param>
/// <param name="Failure">Most recent structured sync failure, when one is available.</param>
/// <param name="LocalDecisionRevision">
/// Installed clear revision required for an explicit restart; zero for a never-published local graph;
/// null when discovery has not yet installed a decision that the user can safely reverse.
/// </param>
/// <param name="Copy">Available cloud copy, or null for a local-only or not-yet-published graph.</param>
/// <param name="Progress">Device-local progress for participating chats; null for local-only participation.</param>
public sealed record ChatSyncItem(
    Guid Id,
    string? Title,
    bool HasLocalCopy,
    ChatSyncCopy? Copy,
    long? LocalDecisionRevision,
    ChatSyncParticipation Participation,
    ChatSyncProgress? Progress,
    ChatSyncFailure? Failure
)
{
    /// <summary>
    /// Last local modification time, or null when only the cloud copy is known.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>
    /// The copy is installed, fully synced and idle, so automatic quota recovery may propose stopping it.
    /// </summary>
    public bool CanReleaseSpace { get; init; }

    /// <summary>
    /// Current participation command, independent of transfer progress.
    /// </summary>
    public ChatSyncOperation Operation { get; init; }

    /// <summary>
    /// Only a participation command disables this chat's management actions.
    /// </summary>
    public bool IsBusy => Operation != ChatSyncOperation.None;

    /// <summary>
    /// The device retains a graph and has a confirmed local-only decision that can be explicitly reversed.
    /// </summary>
    public bool CanStart => HasLocalCopy && Participation == ChatSyncParticipation.LocalOnly && LocalDecisionRevision.HasValue;
}

/// <summary>
/// User participation decision. Pausing the device or a failed transfer does not change it.
/// </summary>
public enum ChatSyncParticipation
{
    LocalOnly,
    Cloud
}

/// <summary>
/// This device's pending, active or completed state for a participating chat.
/// </summary>
public enum ChatSyncProgress
{
    Pending,
    Synced,
    Failed,
    Syncing
}

/// <summary>
/// A participation command currently owned by this session for one chat.
/// </summary>
public enum ChatSyncOperation
{
    None,
    Starting,
    Stopping
}

/// <summary>
/// One coherent, immutable publication. Observers read the latest State when notified,
/// rather than replaying old snapshots queued before an account change.
/// </summary>
public sealed record ChatSyncSessionState(
    ChatSyncInventory? Inventory,
    bool IsSynchronizing,
    Exception? Error
)
{
    /// <summary>
    /// Initial data is missing and no initial failure has been reported.
    /// </summary>
    public bool IsPreparing => Inventory is null && Error is null;

    /// <summary>
    /// At least one participating upload still cannot fit in the current quota.
    /// </summary>
    public bool HasQuotaBlockedChats => Inventory is { } inventory && inventory.Items.AsValueEnumerable().Any(item =>
        item.Participation == ChatSyncParticipation.Cloud && item.Failure?.Quota?.BlocksUpload(inventory.Usage) == true);
}
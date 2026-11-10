using Everywhere.Cloud;
using MessagePack;

namespace Everywhere.Database;

// Authenticated subjects and extensible record keys intentionally remain unbounded SQLite TEXT.
// ReSharper disable EntityFramework.ModelValidation.UnlimitedStringLength
/// <summary>
/// Account-scoped discovery and local intent counters. Enrollment is explicit.
/// </summary>
public sealed class ChatSyncAccountEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Server sync generation accepted by this local account enrollment.
    /// </summary>
    public long Generation { get; set; }

    /// <summary>
    /// Last server discovery revision committed with its complete head page.
    /// </summary>
    public long ScanCursor { get; set; }

    /// <summary>
    /// Monotonic local edit counter used to acknowledge only the captured writes.
    /// </summary>
    public long LocalRevision { get; set; }
}

/// <summary>
/// Durable discovery versus installation progress. No chat foreign key: remote-only
/// chats, pending deletions, and cleared content must survive local chat removal.
/// </summary>
public sealed class ChatSyncChatEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Conversation identity shared by the local graph and this account's cloud copy.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Latest server head seen by discovery, which may be ahead of local installation.
    /// </summary>
    public long DiscoveredRevision { get; set; }

    /// <summary>
    /// Last snapshot revision atomically installed into local history.
    /// </summary>
    public long AppliedRevision { get; set; }

    /// <summary>
    /// The installed head cleared or removed the cloud content; edits alone cannot republish it.
    /// </summary>
    public bool IsContentUnavailable { get; set; }

    /// <summary>
    /// MessagePack head bytes. Check AppliedRevision before using them as a capture baseline.
    /// </summary>
    public required byte[] Head { get; set; }

    /// <summary>
    /// Discovery may be ahead of installed data. Only the installed head supplies
    /// a baseline for freezing a new upload.
    /// </summary>
    public ChatSyncHead? ReadAppliedHead()
    {
        var head = MessagePackSerializer.Deserialize<ChatSyncHead>(Head);
        return head.Revision == AppliedRevision ? head : null;
    }
}

/// <summary>
/// Confirmed revisions by record. Current values belong to Chats and Nodes,
/// independently of synchronization baselines and account enrollment.
/// </summary>
public sealed class ChatSyncRemoteEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Conversation identity shared by the local graph and this account's cloud copy.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Whether this key identifies a node payload, branch choice, field or lifecycle intent.
    /// </summary>
    public required ChatSyncRecordKind Kind { get; set; }

    /// <summary>
    /// Canonical node UUID, metadata-field name or lifecycle action key, according to Kind.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Confirmed server revision of this record, independent of other records in the chat.
    /// </summary>
    public long Revision { get; set; }
}

/// <summary>
/// Coalesced local edit intent. BaseRevision is retained until this edit is resolved.
/// </summary>
public sealed class ChatSyncPendingEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Conversation identity shared by the local graph and this account's cloud copy.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Whether this key identifies a node payload, branch choice, field or lifecycle intent.
    /// </summary>
    public required ChatSyncRecordKind Kind { get; set; }

    /// <summary>
    /// Canonical node UUID, metadata-field name or lifecycle action key, according to Kind.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Monotonic local edit counter used to acknowledge only the captured writes.
    /// </summary>
    public long LocalRevision { get; set; }

    /// <summary>
    /// Server record revision against which the first unresolved local edit was made.
    /// </summary>
    public long BaseRevision { get; set; }
}

/// <summary>
/// Frozen upload identity, wire header, and captured local revisions. New local writes
/// do not alter this request while its network outcome is uncertain.
/// </summary>
public sealed class ChatSyncSubmissionEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Conversation identity shared by the local graph and this account's cloud copy.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Persisted idempotency identity reused until the exact submission has a definite outcome.
    /// </summary>
    public Guid CommitId { get; set; }

    /// <summary>
    /// Serialized immutable upload header reused verbatim across uncertain retries.
    /// </summary>
    public required byte[] Header { get; set; }

    /// <summary>
    /// Serialized record keys and local revisions captured by this submission.
    /// </summary>
    public required byte[] CapturedChanges { get; set; }

    /// <summary>
    /// Serialized definitive quota rejection, or null while the submission is unresolved.
    /// </summary>
    public byte[]? QuotaRejection { get; set; }
}

/// <summary>
/// Ordered wire parts retained until the submission is acknowledged or explicitly rebased.
/// </summary>
public sealed class ChatSyncPartEntity
{
    /// <summary>
    /// Stable authenticated account subject, not a display name or email address.
    /// </summary>
    public required string AccountId { get; set; }

    /// <summary>
    /// Conversation identity shared by the local graph and this account's cloud copy.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Zero-based part index within this frozen submission.
    /// </summary>
    public int Position { get; set; }

    /// <summary>
    /// Exact serialized upload-part bytes, including the part envelope.
    /// </summary>
    public required byte[] Body { get; set; }
}
using System.Runtime.Serialization;
using System.Text.Json;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Cloud;

/// <summary>
/// Wire limits shared with the v2 Worker. UUIDs use lowercase D strings, timestamps
/// use round-trip UTC strings, and counters stay within JavaScript's exact integer range.
/// Message payloads and field values remain opaque MessagePack binary values.
/// </summary>
public static class ChatSyncProtocol
{
    /// <summary>
    /// Wire-protocol generation advertised on each v2 HTTP request.
    /// </summary>
    public const int Version = 2;

    /// <summary>
    /// Largest exact JavaScript integer; all wire revisions, sizes and cursors stay within this inclusive limit.
    /// </summary>
    public const long MaximumCounter = 9_007_199_254_740_991;

    /// <summary>
    /// Maximum serialized upload-part size in bytes (1 MiB), including its MessagePack envelope.
    /// </summary>
    public const int MaximumPartBytes = 1024 * 1024;
}

/// <summary>
/// First zero-based upload part not yet durably staged. Progress survives interrupted HTTP requests.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncUploadProgress([property: Key("nextPart")] int NextPart);

/// <summary>
/// Client compatibility advertised on every request. This is not the local SQLite schema version.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncCompatibility(
    [property: Key("protocolVersion")] int ProtocolVersion,
    [property: Key("syncGeneration")] long SyncGeneration,
    [property: Key("dataGeneration")] long DataGeneration
);

/// <summary>
/// Immutable submission identity and conditional-write baseline. Actions are update,
/// archive, restore, delete, clear, and republish. Reuse the exact header on uncertain retries.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncUploadHeader(
    [property: Key("commitId")] string CommitId,
    [property: Key("chatId")] string ChatId,
    [property: Key("expectedRevision")] long ExpectedRevision,
    [property: Key("requiredDataGeneration")] long RequiredDataGeneration,
    [property: Key("action")] ChatSyncAction Action,
    [property: Key("expectedLifecycleRevision")] long ExpectedLifecycleRevision,
    [property: Key("isArchived")] bool IsArchived = false
);

/// <summary>
/// Node topology and the payload baseline, independent of selected-child revisions.
/// Existing parentage, kind, creation time, and turn membership cannot be rewritten.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncNodeHeader(
    [property: Key("id")] string Id,
    [property: Key("parentId")] string? ParentId,
    [property: Key("turnId")] string? TurnId,
    [property: Key("kind")] int Kind,
    [property: Key("createdAt")] string CreatedAt,
    [property: Key("summaryThroughNodeId")] string? SummaryThroughNodeId,
    [property: Key("expectedPayloadRevision")] long ExpectedPayloadRevision
);

/// <summary>
/// One ordered upload part. Type selects node, choice, or field; unused map keys are
/// ignored by the Worker. A node's opaque body may span multiple offset-checked parts.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncUploadPart
{
    /// <summary>
    /// Selects a node-body chunk, durable branch choice or metadata field.
    /// </summary>
    [Key("type")]
    public required ChatSyncRecordKind Type { get; init; }

    /// <summary>
    /// Node identity and baseline, present only for node-body chunks.
    /// </summary>
    [Key("node")]
    public ChatSyncNodeHeader? Node { get; init; }

    /// <summary>
    /// Zero-based byte offset within the serialized node payload, not the HTTP response or part list.
    /// </summary>
    [Key("offset")]
    public long Offset { get; init; }

    /// <summary>
    /// This chunk completes the current node payload.
    /// </summary>
    [Key("isComplete")]
    public bool IsComplete { get; init; }

    /// <summary>
    /// Opaque MessagePack payload bytes for a node chunk or metadata-field value.
    /// </summary>
    [Key("body")]
    public byte[]? Body { get; init; }

    /// <summary>
    /// Node ID for a choice record, as a canonical lowercase UUID.
    /// </summary>
    [Key("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Selected child for a choice; null removes the durable choice.
    /// </summary>
    [Key("childId")]
    public string? ChildId { get; init; }

    /// <summary>
    /// Metadata-field name, including opaque names introduced by newer clients.
    /// </summary>
    [Key("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Latest chat state. Lifecycle and cloud-content availability are independent.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncHead(
    [property: Key("chatId")] string ChatId,
    [property: Key("revision")] long Revision,
    [property: Key("lifecycle")] ChatSyncLifecycle Lifecycle,
    [property: Key("lifecycleRevision")] long LifecycleRevision,
    [property: Key("availability")] ChatSyncAvailability Availability,
    [property: Key("bytes")] long Bytes,
    [property: Key("lastRepublishedRevision")] long LastRepublishedRevision = 0
)
{
    /// <summary>
    /// The local baseline before the first remote snapshot has been installed.
    /// </summary>
    public static ChatSyncHead Initial(Guid chatId)
    {
        return new ChatSyncHead(chatId.ToString("D"), 0, ChatSyncLifecycle.Active, 0, ChatSyncAvailability.Available, 0);
    }
}

/// <summary>
/// A replayable successful commit receipt. Quota bytes count payloads and metadata values/names.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncCommitResult(
    [property: Key("commitId")] string CommitId,
    [property: Key("revision")] long Revision,
    [property: Key("usedBytes")] long UsedBytes,
    [property: Key("limitBytes")] long LimitBytes
);

/// <summary>
/// Discovery progress advances only through the returned, durably registered heads.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncDiscovery(
    [property: Key("heads")] ChatSyncHead[] Heads,
    [property: Key("cursor")] long Cursor,
    [property: Key("hasMore")] bool HasMore,
    [property: Key("usage")] ChatSyncStorageUsage? Usage = null
);

/// <summary>
/// One page of the account's available copies, largest first.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncStoragePage(
    [property: Key("generation")] long Generation,
    [property: Key("usage")] ChatSyncStorageUsage Usage,
    [property: Key("chats")] ChatSyncCopy[] Chats,
    [property: Key("cursor")] string? Cursor
);

/// <summary>
/// An available cloud copy in the space inventory; Bytes measures logical cloud payload/metadata bytes.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncCopy(
    [property: Key("chatId")] string ChatId,
    [property: Key("title")] string? Title,
    [property: Key("bytes")] long Bytes,
    [property: Key("revision")] long Revision,
    [property: Key("lifecycleRevision")] long LifecycleRevision
);

/// <summary>
/// Identifies exactly the copy the user confirmed, including on a lost-reply retry.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncStopConfirmation(
    [property: Key("commitId")] string CommitId,
    [property: Key("expectedRevision")] long ExpectedRevision,
    [property: Key("expectedLifecycleRevision")] long ExpectedLifecycleRevision
);

/// <summary>
/// Account storage measured by the server, including on discovery pages with no changed heads.
/// </summary>
/// <param name="UsedBytes">Logical payload and metadata bytes held by the account, possibly above quota.</param>
/// <param name="LimitBytes">Account allocation in bytes; zero means uploads have no available allocation.</param>
[MessagePackObject]
public sealed partial record ChatSyncStorageUsage(
    [property: Key("usedBytes")] long UsedBytes,
    [property: Key("limitBytes")] long LimitBytes
);

/// <summary>
/// Frozen delta transfer identity and its target chat revision.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncSnapshot(
    [property: Key("snapshotId")] string SnapshotId,
    [property: Key("head")] ChatSyncHead Head
);

/// <summary>
/// Header of one frozen node, choice, or field entry. Body bytes are paged separately.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncSnapshotEntry
{
    /// <summary>
    /// Determines which topology or metadata fields apply to this frozen entry.
    /// </summary>
    [Key("type")]
    public required ChatSyncRecordKind Type { get; init; }

    /// <summary>
    /// Node ID for node/choice entries; absent for metadata fields.
    /// </summary>
    [Key("id")]
    public string? Id { get; init; }

    /// <summary>
    /// Parent node for a node entry; null identifies the graph root.
    /// </summary>
    [Key("parentId")]
    public string? ParentId { get; init; }

    /// <summary>
    /// Owning turn for a node entry; null for the graph root.
    /// </summary>
    [Key("turnId")]
    public string? TurnId { get; init; }

    /// <summary>
    /// Stored ChatMessageKind discriminator for a node entry.
    /// </summary>
    [Key("kind")]
    public int Kind { get; init; }

    /// <summary>
    /// Node creation time as a round-trip UTC string.
    /// </summary>
    [Key("createdAt")]
    public string? CreatedAt { get; init; }

    /// <summary>
    /// Inclusive source boundary of a compression node, otherwise null.
    /// </summary>
    [Key("summaryThroughNodeId")]
    public string? SummaryThroughNodeId { get; init; }

    /// <summary>
    /// Durably selected child for a choice entry, or null for no selection.
    /// </summary>
    [Key("childId")]
    public string? ChildId { get; init; }

    /// <summary>
    /// Metadata-field name; unknown field values are retained as opaque bytes.
    /// </summary>
    [Key("name")]
    public string? Name { get; init; }

    /// <summary>
    /// Server revision of this payload, choice or field, bounded by the frozen chat head.
    /// </summary>
    [Key("revision")]
    public long Revision { get; init; }
}

/// <summary>
/// Bounded opaque bytes for one snapshot entry. Advance position only when complete.
/// </summary>
[MessagePackObject]
public sealed partial record ChatSyncSnapshotPart(
    [property: Key("header")] ChatSyncSnapshotEntry Header,
    [property: Key("body")] byte[] Body,
    [property: Key("isComplete")] bool IsComplete
);

/// <summary>
/// Structured protocol failure. Presentation maps codes to localized UI messages;
/// protocol diagnostics and numerical quota details are not display text.
/// </summary>
[MessagePackObject]
public sealed partial class ChatSyncFailure
{
    /// <summary>
    /// Exact protocol value, including codes introduced by a newer server.
    /// </summary>
    [Key("code")]
    public string RawCode { get; }

    /// <summary>
    /// Untrusted structured diagnostics from the server; not a localized display-message contract.
    /// </summary>
    [Key("details")]
    public Dictionary<string, object> Details { get; }

    /// <summary>
    /// Application enum mapped once from RawCode; unrecognized server codes map to Unknown.
    /// </summary>
    [IgnoreMember]
    public ChatSyncFailureCode Code { get; }

    /// <summary>
    /// Parsed byte counts for quota_exceeded, otherwise null.
    /// </summary>
    [IgnoreMember]
    public ChatSyncQuotaRejection? Quota { get; }

    private static readonly Dictionary<string, ChatSyncFailureCode> Codes = Enum.GetValues<ChatSyncFailureCode>().ToDictionary(ToWireName);

    /// <summary>
    /// Deserializes a wire failure while preserving an unknown raw code and its diagnostic details.
    /// </summary>
    [SerializationConstructor]
    public ChatSyncFailure(string? rawCode, Dictionary<string, object>? details)
    {
        RawCode = rawCode ?? string.Empty;
        Details = details ?? [];
        Code = Codes.GetValueOrDefault(RawCode);
        if (Code == ChatSyncFailureCode.QuotaExceeded)
        {
            Quota = ChatSyncQuotaRejection.FromDetails(Details);
        }
    }

    /// <summary>
    /// Creates a known protocol failure using its canonical snake_case wire code.
    /// </summary>
    public ChatSyncFailure(ChatSyncFailureCode code, Dictionary<string, object> details) : this(ToWireName(code), details)
    {
    }

    private static string ToWireName(ChatSyncFailureCode code) => JsonNamingPolicy.SnakeCaseLower.ConvertName(code.ToString());
}

/// <summary>
/// Application interpretation of the sync protocol's snake_case error names.
/// </summary>
public enum ChatSyncFailureCode
{
    Unknown,
    Forbidden,
    AccountMismatch,
    TransferCapacity,
    UpgradeRequired,
    GenerationMismatch,
    MigrationRequired,
    RevisionConflict,
    PayloadConflict,
    LifecycleConflict,
    ChatDeleted,
    ContentUnavailable,
    QuotaExceeded,
    InvalidGraph,
    InvalidRequest,
    IdempotencyConflict,
    TransferNotFound,
    SyncDisabled,
    TemporaryChat,
}

/// <summary>
/// Growth rejected by the server, with the usage observed at rejection when available.
/// </summary>
public sealed record ChatSyncQuotaRejection(long DeltaBytes, ChatSyncStorageUsage? Usage)
{
    /// <summary>
    /// Additional bytes this rejected submission needs against current usage; never negative.
    /// </summary>
    public long Shortfall(ChatSyncStorageUsage usage) => Math.Max(0, usage.UsedBytes + DeltaBytes - usage.LimitBytes);

    /// <summary>
    /// Whether the account has no allocation or this submission still exceeds current free space.
    /// </summary>
    public bool BlocksUpload(ChatSyncStorageUsage usage) => usage.LimitBytes == 0 || Shortfall(usage) > 0;

    internal static ChatSyncQuotaRejection FromDetails(Dictionary<string, object> details)
    {
        var usage = Read("usedBytes") is { } used && Read("limitBytes") is { } limit ? new ChatSyncStorageUsage(used, limit) : null;
        return new ChatSyncQuotaRejection(Read("deltaBytes") ?? 0, usage);

        long? Read(string key)
        {
            var value = details.GetValueOrDefault(key);
            var number = value switch
            {
                byte or ushort or uint or sbyte or short or int or long => Convert.ToInt64(value),
                ulong unsigned and <= long.MaxValue => (long)unsigned,
                _ => (long?)null
            };
            return number is >= 0 and <= ChatSyncProtocol.MaximumCounter ? number : null;
        }
    }
}

/// <summary>
/// Discriminator for persisted sync records. Only nodes, choices, and fields have transfer parts.
/// </summary>
[MessagePackFormatter(typeof(EnumAsStringFormatter<ChatSyncRecordKind>))]
public enum ChatSyncRecordKind
{
    [EnumMember(Value = "node")] Node,
    [EnumMember(Value = "choice")] Choice,
    [EnumMember(Value = "field")] Field,
    [EnumMember(Value = "lifecycle")] Lifecycle,
}

/// <summary>
/// Action requested by an immutable upload header.
/// </summary>
[MessagePackFormatter(typeof(EnumAsStringFormatter<ChatSyncAction>))]
public enum ChatSyncAction
{
    [EnumMember(Value = "update")] Update,
    [EnumMember(Value = "archive")] Archive,
    [EnumMember(Value = "restore")] Restore,
    [EnumMember(Value = "delete")] Delete,
    [EnumMember(Value = "clear")] Clear,
    [EnumMember(Value = "republish")] Republish,
}

/// <summary>
/// Lifecycle of a conversation at the remote revision.
/// </summary>
[MessagePackFormatter(typeof(EnumAsStringFormatter<ChatSyncLifecycle>))]
public enum ChatSyncLifecycle
{
    [EnumMember(Value = "active")] Active,
    [EnumMember(Value = "archived")] Archived,
    [EnumMember(Value = "deleted")] Deleted,
}

/// <summary>
/// Availability of the retained content, independently of lifecycle.
/// </summary>
[MessagePackFormatter(typeof(EnumAsStringFormatter<ChatSyncAvailability>))]
public enum ChatSyncAvailability
{
    [EnumMember(Value = "available")] Available,
    [EnumMember(Value = "cleared")] Cleared,
    [EnumMember(Value = "expired")] Expired,
}
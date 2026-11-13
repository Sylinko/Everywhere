using Everywhere.Chat;
using Everywhere.Database;
using Everywhere.Serialization;

namespace Everywhere.Storage;

/// <summary>
/// Detached values for one SQLite transaction. Capture runs in the owning root's execution domain;
/// storage never reads a live message while awaiting I/O.
/// </summary>
public sealed record ChatWriteBatch(ChatMetadataWrite Metadata, IReadOnlyList<ChatNodeWrite> Nodes)
{
    /// <summary>
    /// Signed-in account when this chat was queued for retention, not when a delayed write runs.
    /// </summary>
    public string? LocalAccountId { get; init; }

    /// <summary>
    /// Captured local input committed with this graph, independently of synchronization fields.
    /// First persistence transfers the new-chat slot in this same transaction.
    /// </summary>
    public ChatInputWrite? LocalInput { get; init; }

    /// <summary>
    /// Captures payloads, topology, and the exact revisions they acknowledge together.
    /// Embedded conversations are already materialized when constructed or deserialized.
    /// </summary>
    public static CapturedChatWrite Capture(ChatData data)
    {
        var pending = data.PersistenceDirtyState.Capture();
        var nodes = new List<ChatNodeWrite>(pending.Count);
        foreach (var change in pending)
        {
            var node = change.Node;
            var topology = new ChatNodeTopology(
                node.Id, node.Parent?.Id, node.Turn?.Id, node.ChoiceChildId,
                node.MessageKind, node.SummaryThroughNodeId, node.CreatedAt, node.DateModified);
            if ((change.Kind & (ChatNodeChangeKind.Insert | ChatNodeChangeKind.Payload)) == 0)
            {
                nodes.Add(new ChatNodeWrite(topology, change.Kind, null, []));
                continue;
            }

            var capture = ChatPayloadCapture.Capture(change.Node.Message);
            var summaryThrough = capture.SummaryThroughNodeId == Guid.Empty ? data.RootNode.Id : capture.SummaryThroughNodeId;
            var files = capture.Files
                .AsValueEnumerable()
                .DistinctBy(file => file.Sha256)
                .Select(file => new ChatBlobWrite(file.Sha256, file.FilePath, file.MimeType))
                .ToArray();
            nodes.Add(new ChatNodeWrite(topology with { SummaryThroughNodeId = summaryThrough }, change.Kind, capture.Payload, files));
        }

        var metadata = ChatMetadataWrite.Capture(data.Metadata, out var metadataChanges);
        return new CapturedChatWrite(new ChatWriteBatch(metadata, nodes), pending, metadataChanges);
    }
}

/// <summary>
/// Captured metadata fields written by ordinary saves. Archive state has its own explicit operation.
/// </summary>
public sealed record ChatMetadataWrite(
    ChatV2Entity Snapshot,
    IReadOnlyList<ChatMetadataField> ChangedFields,
    bool ShouldWriteRetention)
{
    /// <summary>
    /// Captures the current metadata without retaining its observable object.
    /// </summary>
    public static ChatMetadataWrite Capture(ChatMetadata metadata, out ChatMetadataChange changes)
    {
        changes = metadata.CaptureChanges();
        return new ChatMetadataWrite(
            ChatMetadataMapper.Capture(metadata), changes.FieldRevisions.Keys.ToArray(), changes.RetentionRevision != 0);
    }
}

/// <summary>
/// A node's frozen relationships and optional replacement payload.
/// </summary>
public sealed record ChatNodeWrite(
    ChatNodeTopology Topology,
    ChatNodeChangeKind Kind,
    byte[]? Payload,
    IReadOnlyList<ChatBlobWrite> Files);

/// <summary>
/// A captured blob reference. Inspecting local file metadata is storage I/O, outside the execution domain.
/// </summary>
public sealed record ChatBlobWrite(string Sha256, string LocalPath, string MimeType);

/// <summary>
/// Detached local input and attachment references, using the existing local file persistence contract.
/// </summary>
public sealed record ChatInputWrite(byte[]? Payload, IReadOnlyList<ChatBlobWrite> Files)
{
    /// <summary>
    /// This input owns the single unsent new-chat slot until its conversation is persisted.
    /// The flag does not apply to standalone selection messages.
    /// </summary>
    public bool IsNewChatDraft { get; init; }

    /// <summary>
    /// Captures UI-owned input before asynchronous storage starts.
    /// </summary>
    public static ChatInputWrite Capture(ChatInputState input)
    {
        var files = input.Draft.AttachmentsSource.Items
            .AsValueEnumerable()
            .Concat(input.Edit?.Draft.AttachmentsSource.Items ?? [])
            .OfType<FileAttachment>()
            .DistinctBy(file => file.Sha256)
            .Select(file => new ChatBlobWrite(file.Sha256, file.FilePath, file.MimeType))
            .ToArray();
        return new ChatInputWrite(input.Capture(), files);
    }
}

/// <summary>
/// A detached save and the exact revisions that a successful commit acknowledges.
/// </summary>
public sealed record CapturedChatWrite(ChatWriteBatch Batch, IReadOnlyList<DirtyChatNode> Changes, ChatMetadataChange MetadataChanges);
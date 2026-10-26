using Everywhere.Chat;
using Everywhere.Serialization;

namespace Everywhere.Storage;

/// <summary>
/// Detached values for one SQLite transaction. Capture runs in the owning root's execution domain;
/// storage never reads a live message while awaiting I/O.
/// </summary>
public sealed record ChatWriteBatch(ChatMetadataWrite Metadata, IReadOnlyList<ChatNodeWrite> Nodes)
{
    /// <summary>
    /// Captures payloads, topology, and the exact revisions they acknowledge together.
    /// Embedded conversations are already materialized when constructed or deserialized.
    /// </summary>
    public static (ChatWriteBatch Batch, IReadOnlyList<DirtyChatNode> Changes) Capture(ChatData data)
    {
        var pending = data.CapturePendingWrites();
        var nodes = new List<ChatNodeWrite>(pending.Count);
        foreach (var (change, topology) in pending)
        {
            if ((change.Kind & (ChatNodeChangeKind.Insert | ChatNodeChangeKind.Payload)) == 0)
            {
                nodes.Add(new ChatNodeWrite(topology, null, null, []));
                continue;
            }

            var capture = ChatPayloadCapture.Capture(change.Node.Message);
            var summaryThrough = capture.SummaryThroughNodeId == Guid.Empty ? data.RootNode.Id : capture.SummaryThroughNodeId;
            var files = capture.Files.DistinctBy(file => file.Sha256)
                .Select(file => new ChatBlobWrite(file.Sha256, file.FilePath, file.MimeType)).ToArray();
            nodes.Add(new ChatNodeWrite(topology, capture.Payload, summaryThrough, files));
        }

        return (new ChatWriteBatch(ChatMetadataWrite.Capture(data.Metadata), nodes), pending.Select(item => item.Change).ToArray());
    }
}

/// <summary>
/// Captured metadata fields written by ordinary saves. Archive state has its own explicit operation.
/// </summary>
public sealed record ChatMetadataWrite(Guid Id, string? Topic, DateTimeOffset DateCreated, DateTimeOffset DateModified)
{
    /// <summary>
    /// Captures the current metadata without retaining its observable object.
    /// </summary>
    public static ChatMetadataWrite Capture(ChatMetadata metadata)
    {
        return new ChatMetadataWrite(metadata.Id, metadata.Topic, metadata.DateCreated, metadata.DateModified);
    }
}

/// <summary>
/// A node's frozen relationships and optional replacement payload.
/// </summary>
public sealed record ChatNodeWrite(
    ChatNodeTopologySnapshot Topology,
    byte[]? Payload,
    Guid? SummaryThroughNodeId,
    IReadOnlyList<ChatBlobWrite> Files);

/// <summary>
/// A captured blob reference. Inspecting local file metadata is storage I/O, outside the execution domain.
/// </summary>
public sealed record ChatBlobWrite(string Sha256, string LocalPath, string MimeType);
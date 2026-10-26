namespace Everywhere.Chat;

/// <summary>
/// Identifies the durable parts of a node changed since its last successful save.
/// </summary>
[Flags]
public enum ChatNodeChangeKind
{
    None = 0,
    Insert = 1,
    Relations = 2,
    Payload = 4,
    All = Insert | Relations | Payload,
}

/// <summary>
/// Tracks unacknowledged node changes independently of presentation invalidation.
/// A writer acknowledges a captured revision only after its transaction commits.
/// </summary>
public sealed class ChatPersistenceDirtyState
{
    /// <summary>
    /// Gets whether any node still needs to be saved.
    /// </summary>
    public bool HasChanges => _nodes.Count > 0;

    private readonly Dictionary<Guid, DirtyChatNode> _nodes = new();
    private long _revision;

    internal void Mark(ChatMessageNode node, ChatNodeChangeKind kind)
    {
        if (_nodes.TryGetValue(node.Id, out var previous))
        {
            kind |= previous.Kind;
        }

        _nodes[node.Id] = new DirtyChatNode(node, ++_revision, kind);
    }

    /// <summary>
    /// Captures pending identities and revisions, not live values for serialization.
    /// The root captures these revisions, topology, and payload values in one command,
    /// then acknowledges them in a later command after the transaction commits.
    /// </summary>
    public IReadOnlyList<DirtyChatNode> Capture()
    {
        return _nodes.Values.ToArray();
    }

    /// <summary>
    /// Acknowledges a committed batch. Changes made while it was being saved remain pending.
    /// </summary>
    public void Acknowledge(IEnumerable<DirtyChatNode> committed)
    {
        foreach (var saved in committed)
        {
            if (_nodes.TryGetValue(saved.Node.Id, out var current) && current.Revision == saved.Revision)
            {
                _nodes.Remove(saved.Node.Id);
            }
        }
    }
}

/// <summary>
/// A node identity and the exact revision whose captured values a writer is saving.
/// </summary>
public readonly record struct DirtyChatNode(ChatMessageNode Node, long Revision, ChatNodeChangeKind Kind);
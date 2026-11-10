namespace Everywhere.Chat;

/// <summary>
/// Identifies the durable parts of a node changed since its last successful save.
/// </summary>
[Flags]
public enum ChatNodeChangeKind
{
    None = 0,
    Insert = 1,
    Choice = 2,
    Payload = 4,
    All = Insert | Choice | Payload,
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
        _nodes.TryGetValue(node.Id, out var previous);
        var revision = ++_revision;
        _nodes[node.Id] = new DirtyChatNode(
            node,
            (kind & ChatNodeChangeKind.Insert) != 0 ? revision : previous.InsertRevision,
            (kind & ChatNodeChangeKind.Payload) != 0 ? revision : previous.PayloadRevision,
            (kind & ChatNodeChangeKind.Choice) != 0 ? revision : previous.ChoiceRevision);
    }

    /// <summary>
    /// Captures pending identities and revisions, not live values for serialization.
    /// The root captures these revisions, topology, and payload values in one command,
    /// then acknowledges them in a later command after the transaction commits.
    /// </summary>
    public IReadOnlyList<DirtyChatNode> Capture() => [.. _nodes.Values];

    /// <summary>
    /// Acknowledges a committed batch. Changes made while it was being saved remain pending.
    /// </summary>
    public void Acknowledge(IEnumerable<DirtyChatNode> committed)
    {
        foreach (var saved in committed)
        {
            if (!_nodes.TryGetValue(saved.Node.Id, out var current))
            {
                continue;
            }

            var remaining = current with
            {
                InsertRevision = current.InsertRevision == saved.InsertRevision ? 0 : current.InsertRevision,
                PayloadRevision = current.PayloadRevision == saved.PayloadRevision ? 0 : current.PayloadRevision,
                ChoiceRevision = current.ChoiceRevision == saved.ChoiceRevision ? 0 : current.ChoiceRevision
            };
            if (remaining.Kind == ChatNodeChangeKind.None)
            {
                _nodes.Remove(saved.Node.Id);
            }
            else
            {
                _nodes[saved.Node.Id] = remaining;
            }
        }
    }
}

/// <summary>
/// A node identity and independent revisions for inserts, payloads, and choices.
/// A later payload edit must not keep an already committed choice dirty.
/// </summary>
public readonly record struct DirtyChatNode(ChatMessageNode Node, long InsertRevision, long PayloadRevision, long ChoiceRevision)
{
    /// <summary>
    /// Gets the portions that have not yet been acknowledged.
    /// </summary>
    public ChatNodeChangeKind Kind =>
        (InsertRevision != 0 ? ChatNodeChangeKind.Insert : ChatNodeChangeKind.None) |
        (PayloadRevision != 0 ? ChatNodeChangeKind.Payload : ChatNodeChangeKind.None) |
        (ChoiceRevision != 0 ? ChatNodeChangeKind.Choice : ChatNodeChangeKind.None);
}
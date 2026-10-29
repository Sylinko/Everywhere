using System.Collections.Concurrent;
using MessagePack;

namespace Everywhere.Chat;

partial class ChatData
{
    /// <summary>
    /// Gets pending durable node revisions, independently of turn invalidation.
    /// </summary>
    [IgnoreMember]
    public ChatPersistenceDirtyState PersistenceDirtyState { get; } = new();

    /// <summary>
    /// Gets whether any non-root history is retained, including unselected branches.
    /// An intentionally empty selected path must not suppress saving or erase that history.
    /// </summary>
    [IgnoreMember]
    public bool HasHistory => !_messageNodeMap.IsEmpty;

    /// <summary>
    /// Gets the turns on the displayed branch as a stable reference snapshot.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyList<ChatTurn> SelectedTurns => Volatile.Read(ref _selectedTurns);

    /// <summary>
    /// Gets the assistant that can be continued on the selected branch. Internal notices do not
    /// end eligibility, but a later assistant or user turn replaces the continuation target.
    /// </summary>
    [IgnoreMember]
    public ChatMessageNode? ContinuationTarget
    {
        get
        {
            var nodes = Items;
            for (var index = nodes.Count - 1; index >= 0; index--)
            {
                var node = nodes[index];
                if (node.MessageKind == ChatMessageKind.Assistant)
                {
                    return node;
                }

                if (node.MessageKind.IsTurnStart())
                {
                    return null;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Gets the revision of the displayed path, independent of streaming content.
    /// </summary>
    [IgnoreMember]
    public long BranchRevision => Interlocked.Read(ref _branchRevision);

    /// <summary>
    /// Signals a change to the displayed path after its relationships and indexes are updated.
    /// Raised on the mutation thread; presentation consumers marshal at their ingress.
    /// </summary>
    public event EventHandler? BranchChanged;

    /// <summary>
    /// Signals a durable node mutation for an explicitly owning embedded-data block.
    /// Runtime progress and ordinary presentation invalidation do not cross this bridge.
    /// </summary>
    public event EventHandler? DurableChanged;

    /// <summary>
    /// Signals changes to retained pre-user auxiliary messages, which do not belong to a turn.
    /// </summary>
    public event EventHandler? AuxiliaryInvalidated;

    [IgnoreMember] private readonly ConcurrentDictionary<Guid, ChatTurn> _turnsById = new();
    [IgnoreMember] private IReadOnlyList<ChatTurn> _selectedTurns = [];
    [IgnoreMember] private long _branchRevision;

    /// <summary>
    /// Finds a retained node without changing the selected branch.
    /// </summary>
    public ChatMessageNode? FindNode(Guid id)
    {
        return id == RootNode.Id ? RootNode : _messageNodeMap.GetValueOrDefault(id);
    }

    /// <summary>
    /// Finds a retained turn, including turns outside the displayed branch.
    /// </summary>
    public ChatTurn? FindTurn(Guid id)
    {
        return _turnsById.GetValueOrDefault(id);
    }

    /// <summary>
    /// Captures pending revisions and topology in the root execution domain. The persistence
    /// caller serializes payloads in this same command before allowing another mutation.
    /// </summary>
    public IReadOnlyList<(DirtyChatNode Change, ChatNodeTopologySnapshot Topology)> CapturePendingWrites()
    {
        return PersistenceDirtyState.Capture().AsValueEnumerable().Select(change =>
        {
            var node = change.Node;
            var topology = new ChatNodeTopologySnapshot(
                node.Id,
                node.Parent?.Id,
                node.Turn?.Id,
                node.ChoiceChildId,
                node.MessageKind,
                node.CreatedAt,
                node.DateModified);
            return (change, topology);
        }).ToArray();
    }

    /// <summary>
    /// Captures an execution path by following parents, never the UI's current choices.
    /// The root is included. Message values must be captured separately by the consumer.
    /// </summary>
    public IReadOnlyList<ChatMessageNode> GetPathTo(ChatMessageNode endpoint)
    {
        VerifyOwnedNode(endpoint);

        var nodes = new List<ChatMessageNode>();
        for (var node = endpoint; node is not null; node = node.Parent)
        {
            nodes.Add(node);
        }

        nodes.Reverse();
        return nodes;
    }

    /// <summary>
    /// Appends to an explicit parent. Existing children keep their parent and identities.
    /// Choosing the new child only changes the displayed path if its parent is displayed.
    /// </summary>
    public ChatMessageNode Append(ChatMessageNode parent, ChatMessage message, bool shouldSelectChild = true)
    {
        VerifyOwnedNode(parent);

        var node = new ChatMessageNode(message)
        {
            Data = this,
            Parent = parent,
        };
        AssignTurn(node);
        _messageNodeMap.TryAdd(node.Id, node);
        PersistenceDirtyState.Mark(node, ChatNodeChangeKind.All);

        // Suppress the legacy property notification while the compound topology change is built.
        // The data publishes the completed path once, after both endpoints exist.
        parent.AddChild(node.Id);
        if (shouldSelectChild)
        {
            parent.SetChoiceChild(node.Id);
        }

        RebuildTurnPath(parent.Turn);
        if (!ReferenceEquals(parent.Turn, node.Turn))
        {
            RebuildTurnPath(node.Turn);
        }

        UpdateBranchAfterNode(parent);
        parent.NotifyTopologyChanged();
        NotifyNodeChanged(parent, ChatNodeChangeKind.Relations);
        return node;
    }

    /// <summary>
    /// Selects a direct child, remembering choices on all other branches.
    /// A null child ends this path without deleting any descendants.
    /// </summary>
    public void SelectChild(ChatMessageNode parent, ChatMessageNode? child)
    {
        VerifyOwnedNode(parent);
        if (child is not null && !ReferenceEquals(child.Parent, parent))
        {
            throw new ArgumentException("The selected node must be a direct child of the parent.", nameof(child));
        }

        if (parent.ChoiceChildId == child?.Id)
        {
            return;
        }

        parent.SetChoiceChild(child?.Id);
        RebuildTurnPath(parent.Turn);
        UpdateBranchAfterNode(parent);
        parent.NotifyTopologyChanged();
        NotifyNodeChanged(parent, ChatNodeChangeKind.Relations);
    }

    /// <summary>
    /// Gets the append point for continuing the latest assistant, allowing trailing internal notices.
    /// </summary>
    public ChatMessageNode GetContinuationTail(ChatMessageNode assistantNode)
    {
        VerifyOwnedNode(assistantNode);
        if (!ReferenceEquals(assistantNode, ContinuationTarget))
        {
            throw new InvalidOperationException("Only the latest assistant on the selected branch can be continued.");
        }

        return Items[^1];
    }

    private void VerifyOwnedNode(ChatMessageNode node)
    {
        if (!ReferenceEquals(node.Data, this))
        {
            throw new ArgumentException("The node belongs to a different conversation.", nameof(node));
        }
    }

    private void AssignTurn(ChatMessageNode node)
    {
        if (node.MessageKind.IsTurnStart())
        {
            node.Turn = new ChatTurn(node);
            _turnsById.TryAdd(node.Id, node.Turn);
        }
        else
        {
            node.Turn = node.Parent?.Turn;
        }

        node.Turn?.AddNode(node);
    }

    private void InitializeTurns()
    {
        var pending = new Stack<ChatMessageNode>();
        pending.Push(RootNode);
        while (pending.TryPop(out var parent))
        {
            foreach (var childId in parent.Children)
            {
                var child = _messageNodeMap[childId];
                AssignTurn(child);
                pending.Push(child);
            }
        }

        foreach (var turn in _turnsById.Values)
        {
            RebuildTurnPath(turn);
        }
    }

    private static void RebuildTurnPath(ChatTurn? turn)
    {
        if (turn is null)
        {
            return;
        }

        var nodes = new List<ChatMessageNode>();
        for (var node = turn.UserNode; node is not null && ReferenceEquals(node.Turn, turn); node = node.SelectedChild)
        {
            nodes.Add(node);
        }

        turn.SetSelectedNodes(nodes);
    }

    private void PublishSelectedTurns(IReadOnlyList<ChatMessageNode> nodes)
    {
        var turns = nodes.AsValueEnumerable().Where(node => node.Turn?.UserNode == node).Select(node => node.Turn).OfType<ChatTurn>().ToArray();
        Volatile.Write(ref _selectedTurns, turns);
        Interlocked.Increment(ref _branchRevision);
        OnPropertyChanged(nameof(HasHistory));
        BranchChanged?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// Immutable durable topology values for a node-scoped database write.
/// </summary>
public readonly record struct ChatNodeTopologySnapshot(
    Guid Id,
    Guid? ParentId,
    Guid? TurnId,
    Guid? ChoiceChildId,
    ChatMessageKind MessageKind,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt
);
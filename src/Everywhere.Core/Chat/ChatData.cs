using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Messages;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Maintains the data of the chat, including a tree of <see cref="ChatMessageNode"/> and other metadata.
/// The current branch is derived by following each node's <see cref="ChatMessageNode.ChoiceChildId"/>.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class ChatData : ObservableObject, IDisposable
{
    [Key(0)]
    public ChatMetadata Metadata { get; }

    /// <summary>
    /// Nodes in the current branch, including the hidden root node.
    /// </summary>
    [IgnoreMember]
    public int Count => Items.Count;

    /// <summary>
    /// Gets a snapshot of the current branch, including the hidden root node.
    /// Topology mutations publish a new reference snapshot. Reading never copies or loads bodies.
    /// Nodes and messages retain their canonical identities and remain mutable.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyList<ChatMessageNode> Items => Volatile.Read(ref _items);

    /// <summary>
    /// Backing store for MessagePack (de)serialization: nodes are persisted as a collection, and linked by Ids.
    /// </summary>
    [Key(1)]
    private ICollection<ChatMessageNode> MessageNodes => _messageNodeMap.Values;

    /// <summary>
    /// Gets the topology root. It is not a user turn and has no turn membership.
    /// </summary>
    [Key(2)]
    public ChatMessageNode RootNode { get; }

    /// <summary>
    /// Map of all message nodes by their ID. This allows for quick access to any node in the data.
    /// This map excludes the root node; both legacy empty and newer nonempty root IDs are supported.
    /// </summary>
    // One runtime writer, concurrent UI lookups. Published branch arrays carry traversal order.
    [IgnoreMember] private readonly ConcurrentDictionary<Guid, ChatMessageNode> _messageNodeMap = new();

    /// <summary>
    /// Nodes on the currently selected branch. [0] is always the root node.
    /// </summary>
    [IgnoreMember] private IReadOnlyList<ChatMessageNode> _items;

    /// <summary>
    /// Gets whether this data has released its message resources.
    /// </summary>
    [IgnoreMember]
    public bool IsDisposed => Volatile.Read(ref _isDisposed);

    [IgnoreMember] private bool _isDisposed;

    /// <summary>
    /// Constructor for MessagePack deserialization and for creating conversation data with existing nodes.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="messageNodes"></param>
    /// <param name="rootNode"></param>
    [SerializationConstructor]
    public ChatData(ChatMetadata metadata, ICollection<ChatMessageNode> messageNodes, ChatMessageNode rootNode)
    {
        Metadata = metadata;
        _messageNodeMap.AddRange(messageNodes.Select(v => new KeyValuePair<Guid, ChatMessageNode>(v.Id, v)));
        RootNode = rootNode;
        _items = [rootNode];

        foreach (var node in messageNodes.Append(rootNode))
        {
            node.Data = this;
            node.SortChildren();
            foreach (var childId in node.Children) _messageNodeMap[childId].Parent = node;
        }

        if (_messageNodeMap.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Message nodes cannot contain a node with an empty ID.");

        InitializeTurns();
        UpdateBranchAfter(0, rootNode);
    }

    /// <summary>
    /// Creates new conversation data. A new Guid v7 ID is assigned.
    /// </summary>
    public ChatData()
    {
        Metadata = new ChatMetadata(Guid.CreateVersion7(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        RootNode = new ChatMessageNode(Guid.CreateVersion7().SetVersion(0), new RootChatMessage())
        {
            Data = this
        };
        _items = [RootNode];
        PersistenceDirtyState.Mark(RootNode, ChatNodeChangeKind.All);
        UpdateBranchAfter(0, RootNode);
    }

    /// <summary>
    /// Create a new branch on the specified sibling node by inserting a new message at that position.
    /// </summary>
    public ChatMessageNode CreateBranchOn(ChatMessageNode siblingNode, ChatMessage chatMessage)
    {
        VerifyOwnedNode(siblingNode);
        var parent = siblingNode.Parent ?? throw new InvalidOperationException("The root has no siblings.");
        return Append(parent, chatMessage, shouldSelectChild: parent.ChoiceChildId == siblingNode.Id);
    }

    /// <summary>
    /// Adds a message at the end of the displayed branch. Generation code uses an explicit parent.
    /// </summary>
    public ChatMessageNode Add(ChatMessage message)
    {
        return Append(Items[^1], message);
    }

    /// <summary>
    /// Gets all nodes in the chat data in all branches, including the root node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<ChatMessageNode> GetAllNodes()
    {
        var nodes = _messageNodeMap.Values;
        var results = new ChatMessageNode[nodes.Count + 1];
        results[0] = RootNode;
        nodes.CopyTo(results, 1);
        return results;
    }

    internal void NotifyNodeChanged(ChatMessageNode node, ChatNodeChangeKind kind)
    {
        if (kind != ChatNodeChangeKind.None)
        {
            PersistenceDirtyState.Mark(node, kind);
            Metadata.DateModified = DateTimeOffset.UtcNow;
            WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(Metadata, nameof(Metadata.DateModified)));
            DurableChanged?.Invoke(this, EventArgs.Empty);
        }

        // A user row's selector describes its parent's children, which may belong to the
        // preceding turn (or the root). Refresh that row even when the selected path is unchanged.
        if ((kind & ChatNodeChangeKind.Relations) != 0 && node.SelectedChild?.Turn is { } childTurn &&
            !ReferenceEquals(childTurn, node.Turn))
        {
            childTurn.Invalidate();
        }

        if (node.Turn is { } turn)
        {
            turn.Invalidate();
        }
        else
        {
            AuxiliaryInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Rebuilds the current branch from the specified node forward.
    /// </summary>
    private void UpdateBranchAfterNode(ChatMessageNode node)
    {
        var nodes = Items;
        for (var index = 0; index < nodes.Count; index++)
        {
            if (ReferenceEquals(nodes[index], node))
            {
                UpdateBranchAfter(index, node);
                return;
            }
        }
    }

    private void UpdateBranchAfter(int index, ChatMessageNode node)
    {
        var previous = Items;
        var selected = previous.Take(index + 1).ToList();
        while (node.SelectedChild is { } child)
        {
            selected.Add(child);
            node = child;
        }

        if (previous.SequenceEqual(selected))
        {
            return;
        }

        var snapshot = selected.ToArray();
        Volatile.Write(ref _items, snapshot);
        PublishSelectedTurns(snapshot);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        foreach (var node in _messageNodeMap.Values)
        {
            node.Dispose();
        }

        RootNode.Dispose();
    }
}
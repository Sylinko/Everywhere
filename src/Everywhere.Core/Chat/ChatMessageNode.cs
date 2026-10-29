using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// A stable history node with turn membership and a remembered chosen-child identity.
/// ChoiceIndex is a derived presentation/legacy-serialization view of that identity.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class ChatMessageNode : IDisposable
{
    [Key(0)]
    public Guid Id { get; }

    [Key(1)]
    public ChatMessage Message => _message ?? throw new InvalidOperationException("Load this node's turn before reading its message body.");

    /// <summary>
    /// Gets the durable message-type projection without loading the body.
    /// </summary>
    [IgnoreMember]
    public ChatMessageKind MessageKind { get; }

    /// <summary>
    /// Gets whether the message body has been materialized.
    /// </summary>
    [IgnoreMember]
    public bool IsMessageLoaded => Volatile.Read(ref _message) is not null;

    /// <summary>
    /// Gets the successful summary's coverage anchor from the lightweight topology.
    /// </summary>
    [IgnoreMember]
    public Guid? SummaryThroughNodeId { get; internal set; }

    /// <summary>
    /// Gets the persisted node creation time, independently of body loading.
    /// </summary>
    [IgnoreMember]
    public DateTimeOffset CreatedAt { get; }

    [Key(2)]
    public IReadOnlyList<Guid> Children => Volatile.Read(ref _children);

    /// <summary>
    /// Index of the chosen child in <see cref="Children"/> (-1 when none).
    /// When persisted, it should be mapped to the child's ID (ChoiceChildId) to avoid index drift under concurrent inserts.
    /// </summary>
    [Key(3)]
    public int ChoiceIndex
    {
        get => ChoiceChildId is { } id ? Array.IndexOf(_children, id) : -1;
        init
        {
            var index = Math.Clamp(value, -1, Children.Count - 1);
            var childId = index >= 0 ? Children[index] : (Guid?)null;
            if (_data is null)
            {
                SetChoiceChild(childId);
            }
            else
            {
                _data.SelectChild(this, childId is { } id ? _data.FindNode(id) : null);
            }
        }
    }

    /// <summary>
    /// Gets the remembered child identity independently of its sibling position.
    /// </summary>
    [IgnoreMember]
    public Guid? ChoiceChildId { get; private set; }

    /// <summary>
    /// Gets the remembered child, or null when this path ends.
    /// </summary>
    [IgnoreMember]
    public ChatMessageNode? SelectedChild => ChoiceChildId is { } id ? Data.FindNode(id) : null;

    /// <summary>
    /// Gets the user turn containing this node. Pre-user auxiliary nodes have no turn.
    /// </summary>
    [IgnoreMember]
    public ChatTurn? Turn { get; internal set; }

    [IgnoreMember]
    public int ChoiceCount => Children.Count;

    [IgnoreMember]
    public ChatMessageNode? Parent { get; internal set; }

    [IgnoreMember]
    public ChatData Data
    {
        get => _data ?? throw new InvalidOperationException("This node is not attached to a ChatData.");
        internal set => _data = value;
    }

    [IgnoreMember]
    public DateTimeOffset DateModified { get; private set; }

    [IgnoreMember] private bool _isDisposed;
    [IgnoreMember] private ChatMessage? _message;
    [IgnoreMember] private Guid[] _children = [];
    [IgnoreMember] private ChatData? _data;

    /// <summary>
    /// Creates a new ChatMessageNode with a new Version 7 GUID.
    /// </summary>
    /// <param name="message"></param>
    public ChatMessageNode(ChatMessage message) : this(Guid.CreateVersion7(), message) { }

    /// <summary>
    /// Creates a new ChatMessageNode with the given ID.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="message"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public ChatMessageNode(Guid id, ChatMessage message)
    {
        Id = id;
        _message = message ?? throw new ArgumentNullException(nameof(message)); // messagepack may pass null here so we guard against it
        MessageKind = message.Kind;
        CreatedAt = message.CreatedAt;
        SummaryThroughNodeId = message is ContextCompressionChatMessage { HasSummary: true } compression ? compression.CoveredThroughNodeId : null;
        message.Owner = this;
        DateModified = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Creates a lightweight node from persisted topology without materializing a message body.
    /// </summary>
    public ChatMessageNode(Guid id, ChatMessageKind messageKind, DateTimeOffset createdAt, DateTimeOffset updatedAt)
    {
        Id = id;
        MessageKind = messageKind;
        CreatedAt = createdAt;
        DateModified = updatedAt;
    }

    /// <summary>
    /// Installs a loaded body only while this node still has no body. Existing in-memory content wins.
    /// </summary>
    public bool TryLoadMessage(ChatMessage message)
    {
        if (_isDisposed || _message is not null)
        {
            return false;
        }

        if (message.Kind != MessageKind)
        {
            throw new InvalidDataException("The loaded message kind does not match its topology projection.");
        }

        message.Owner = this;
        Volatile.Write(ref _message, message);
        return true;
    }

    [SerializationConstructor]
    private ChatMessageNode(Guid id, ChatMessage message, IReadOnlyList<Guid> children, int choiceIndex) : this(id, message)
    {
        _children = [.. children];
        ChoiceIndex = choiceIndex;
    }

    internal void NotifyMessageChanged(bool isDurable)
    {
        if (_message is ContextCompressionChatMessage { HasSummary: true } compression)
        {
            SummaryThroughNodeId = compression.CoveredThroughNodeId;
        }

        if (isDurable)
        {
            DateModified = DateTimeOffset.UtcNow;
        }

        _data?.NotifyNodeChanged(this, isDurable ? ChatNodeChangeKind.Payload : ChatNodeChangeKind.None);
    }

    internal void AddChild(Guid childId)
    {
        PublishChildren([.. _children, childId]);
    }

    internal void SortChildren()
    {
        PublishChildren([.. _children]);
    }

    private void PublishChildren(Guid[] children)
    {
        // Detached storage nodes are linked before their ChatData exists. The data constructor
        // sorts them after attachment, preserving choices already resolved from legacy indices.
        if (_data is { } data)
        {
            Array.Sort(children, (left, right) =>
            {
                var leftNode = data.FindNode(left) ?? throw new InvalidDataException("A child node is missing.");
                var rightNode = data.FindNode(right) ?? throw new InvalidDataException("A child node is missing.");
                var comparison = leftNode.CreatedAt.CompareTo(rightNode.CreatedAt);
                if (comparison != 0)
                {
                    return comparison;
                }

                // Use unsigned, big-endian UUID bytes rather than database-specific GUID ordering.
                var leftBytes = (Span<byte>)stackalloc byte[16];
                var rightBytes = (Span<byte>)stackalloc byte[16];
                left.TryWriteBytes(leftBytes, bigEndian: true, out _);
                right.TryWriteBytes(rightBytes, bigEndian: true, out _);
                return leftBytes.SequenceCompareTo(rightBytes);
            });
        }

        Volatile.Write(ref _children, children);
    }

    internal void SetChoiceChild(Guid? childId)
    {
        ChoiceChildId = childId;
    }

    internal void NotifyTopologyChanged()
    {
        DateModified = DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_message is { } message)
        {
            message.Owner = null;
            if (message is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}
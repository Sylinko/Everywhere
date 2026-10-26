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

    /// <summary>
    /// Gets the stable append order among children of the same parent.
    /// </summary>
    [IgnoreMember]
    public long SiblingOrder { get; internal set; }

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
        Volatile.Write(ref _children, [.. _children, childId]);
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
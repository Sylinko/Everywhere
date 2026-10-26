namespace Everywhere.Chat;

/// <summary>
/// Owns the runtime identity of a user turn, including all of its response branches.
/// Nodes remain owned by the data when this turn is not on the displayed branch.
/// </summary>
public sealed class ChatTurn(ChatMessageNode userNode)
{
    /// <summary>
    /// Gets the stable turn identifier, equal to the starting user node's identifier.
    /// </summary>
    public Guid Id => UserNode.Id;

    /// <summary>
    /// Gets the user message that starts this turn.
    /// </summary>
    public ChatMessageNode UserNode { get; } = userNode;

    /// <summary>
    /// Gets the remembered local path, stopping before the next user turn.
    /// The collection is a reference snapshot; message bodies remain mutable.
    /// </summary>
    public IReadOnlyList<ChatMessageNode> SelectedNodes => Volatile.Read(ref _selectedNodes);

    /// <summary>
    /// Gets the invalidation revision. Capture it before reading presentation values.
    /// </summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Gets runtime operations, including retained completions, independently of window materialization.
    /// </summary>
    public IReadOnlyList<ChatBusyActivity> Activities => Volatile.Read(ref _activities);

    /// <summary>
    /// Signals that a later snapshot is needed. Raised on the mutation thread.
    /// </summary>
    public event EventHandler? Invalidated;

    public IReadOnlyList<ChatMessageNode> Nodes => _nodes;

    private readonly List<ChatMessageNode> _nodes = [];
    private IReadOnlyList<ChatMessageNode> _selectedNodes = [userNode];
    private long _revision;
    private IReadOnlyList<ChatBusyActivity> _activities = [];

    public void AddNode(ChatMessageNode node)
    {
        _nodes.Add(node);
    }

    internal void SetSelectedNodes(IReadOnlyList<ChatMessageNode> nodes)
    {
        Volatile.Write(ref _selectedNodes, nodes);
    }

    public void Invalidate()
    {
        Interlocked.Increment(ref _revision);
        Invalidated?.Invoke(this, EventArgs.Empty);
    }

    public void AddActivity(ChatBusyActivity activity)
    {
        Volatile.Write(ref _activities, _activities.AsValueEnumerable().Append(activity).ToArray());

        Invalidate();
    }

    public void CompleteActivity(ChatBusyActivity activity)
    {
        if (activity.RemoveAfterCompletion)
        {
            Volatile.Write(ref _activities, _activities.AsValueEnumerable().Where(candidate => !ReferenceEquals(candidate, activity)).ToArray());
        }

        Invalidate();
    }
}
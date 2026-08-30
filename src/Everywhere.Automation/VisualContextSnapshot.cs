namespace Everywhere.Automation;

/// <summary>
/// Contains one bounded, potentially partial observation snapshot of the live visual tree.
/// </summary>
public sealed class VisualContextSnapshot : IDisposable
{
    /// <summary>
    /// Gets the ordered roots retained by the snapshot phase.
    /// </summary>
    public IReadOnlyList<VisualContextSnapshotNode> Roots { get; }

    private VisualElementRetention Retention { get; }

    /// <summary>
    /// Initializes a Snapshot over a completed graph-build retention. The caller transfers ownership of the retention to this Snapshot.
    /// </summary>
    public VisualContextSnapshot(VisualElementRetention retention, IReadOnlyList<VisualContextSnapshotNode> roots)
    {
        Retention = retention;
        Roots = roots;
    }

    /// <summary>
    /// Releases every element retained only for this Snapshot. Agent-visible targets must be published before disposal.
    /// </summary>
    public void Dispose() => Retention.Dispose();
}

/// <summary>
/// Contains safely observed facts and traversal metadata for one visual element.
/// </summary>
/// <remarks>
/// The retained <see cref="Element"/> is an action and follow-up-query handle. Planning and prompt
/// construction must use <see cref="Snapshot"/> and must not perform platform reads through it.
/// </remarks>
public sealed class VisualContextSnapshotNode
{
    /// <summary>
    /// Gets the Context-owned platform element retained for later queries, publication, and actions.
    /// </summary>
    public required VisualElement Element { get; init; }

    /// <summary>
    /// Gets the bounded scalar facts observed while creating the visual-context snapshot.
    /// </summary>
    public required VisualElementSnapshot Snapshot { get; init; }

    /// <summary>
    /// Gets the requested fields that were safely observed.
    /// </summary>
    public required VisualElementFields AvailableFields { get; init; }

    /// <summary>
    /// Gets the requested fields that were unavailable or incomplete.
    /// </summary>
    public required VisualElementFields MissingFields { get; init; }

    /// <summary>
    /// Gets this node's retained parent, or <see langword="null"/> when this node is a snapshot root.
    /// </summary>
    public VisualContextSnapshotNode? Parent { get; private set; }

    /// <summary>
    /// Gets the ordered children observed within the snapshot limits.
    /// </summary>
    public IReadOnlyList<VisualContextSnapshotNode> Children => _children;

    /// <summary>
    /// Gets the distance accumulated within the originating core traversal.
    /// </summary>
    public int LocalDistance { get; init; }

    /// <summary>
    /// Gets the normalized distance used to compare nodes across snapshot roots.
    /// </summary>
    public int GlobalDistance { get; init; }

    /// <summary>
    /// Gets the weighted traversal priority assigned while creating the snapshot.
    /// </summary>
    public float TraversalPriority { get; init; }

    /// <summary>
    /// Gets the monotonic order in which snapshot traversal committed this node.
    /// </summary>
    public long TraversalOrdinal { get; init; }

    /// <summary>
    /// Gets whether this node originated from a caller-supplied core element.
    /// </summary>
    public bool IsCore { get; init; }

    /// <summary>
    /// Gets whether this node exposes an independently useful interaction.
    /// </summary>
    public bool IsInteractive { get; init; }

    /// <summary>
    /// Gets bounded Agent-facing explanations for incomplete or degraded observation.
    /// </summary>
    public IReadOnlyList<string> Status => _status;

    private readonly List<VisualContextSnapshotNode> _children = [];
    private readonly List<string> _status = [];

    /// <summary>
    /// Appends an observed child and establishes its parent relationship.
    /// </summary>
    /// <param name="child">The child to append in observation order.</param>
    public void AddChild(VisualContextSnapshotNode child)
    {
        if (ReferenceEquals(child, this) || child.Parent is not null)
        {
            throw new InvalidOperationException("A visual-context snapshot node cannot contain itself or belong to more than one parent.");
        }

        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>
    /// Appends one bounded Agent-facing status message.
    /// </summary>
    /// <param name="status">The status message to append.</param>
    public void AddStatus(string status) => _status.Add(status);
}
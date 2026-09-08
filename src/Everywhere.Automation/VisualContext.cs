using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

/// <summary>
/// Represents the platform-neutral visual identity, lifetime, and Agent-target domain associated with one owning chat context.
/// </summary>
public sealed class VisualContext : IDisposable
{
    /// <summary>
    /// Gets the default maximum number of completed conversation turns retained for historical lookup.
    /// </summary>
    public const int DefaultMaximumRetainedTurnCount = 8;

    /// <summary>
    /// Gets the default soft maximum number of distinct Agent targets retained by completed turns.
    /// </summary>
    public const int DefaultMaximumRetainedTargetCount = 2048;

    /// <summary>
    /// Gets the number of distinct Agent identifiers retained by the current and historical turns.
    /// </summary>
    public int TargetCount => _targetTurnCounts.Count;

    /// <summary>
    /// Gets the number of completed conversation turns currently retained for historical lookup.
    /// </summary>
    public int RetainedTurnCount => _retainedTurns.Count;

    /// <summary>
    /// Gets the next monotonically allocated Agent identifier. Identifiers are never reused within this Context.
    /// </summary>
    public int NextTargetId { get; private set; } = 1;

    /// <summary>
    /// Gets the maximum number of completed conversation turns retained for historical lookup.
    /// </summary>
    public int MaximumRetainedTurnCount { get; }

    /// <summary>
    /// Gets the soft maximum number of distinct Agent targets retained by completed conversation turns.
    /// </summary>
    /// <remarks>
    /// The newest completed turn is preserved even when it alone exceeds this limit, so targets just returned to the Agent remain resolvable.
    /// </remarks>
    public int MaximumRetainedTargetCount { get; }

    private readonly Dictionary<Type, object> _identityMaps = [];
    private readonly HashSet<VisualElementRetention> _retentions = [];
    private readonly LinkedList<VisualTargetTurn> _retainedTurns = [];
    private readonly Dictionary<int, int> _targetTurnCounts = [];
    private VisualTargetTurn? _activeTurn;
    private long _publicationVersion;
    private bool _isDisposed;

    /// <summary>
    /// Initializes one visual identity, lifetime, and Agent-target domain.
    /// </summary>
    /// <param name="maximumRetainedTurnCount">The maximum number of completed turns retained for historical lookup.</param>
    /// <param name="maximumRetainedTargetCount">The soft maximum number of distinct targets retained by completed turns.</param>
    public VisualContext(
        int maximumRetainedTurnCount = DefaultMaximumRetainedTurnCount,
        int maximumRetainedTargetCount = DefaultMaximumRetainedTargetCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedTurnCount);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetainedTargetCount);
        MaximumRetainedTurnCount = maximumRetainedTurnCount;
        MaximumRetainedTargetCount = maximumRetainedTargetCount;
    }

    /// <summary>
    /// Creates one logical ownership batch for an attachment, Snapshot, or other bounded owner.
    /// </summary>
    public VisualElementRetention CreateRetention()
    {
        ThrowIfDisposed();
        var retention = new VisualElementRetention(this);
        _retentions.Add(retention);
        return retention;
    }

    /// <summary>
    /// Begins the conversation turn that will own every target published or successfully looked up before completion.
    /// </summary>
    public VisualTargetTurn BeginTurn()
    {
        ThrowIfDisposed();
        if (_activeTurn is not null)
        {
            throw new InvalidOperationException("A VisualContext cannot have more than one active conversation turn.");
        }

        var turn = new VisualTargetTurn(this, CreateRetention());
        _activeTurn = turn;
        return turn;
    }

    /// <summary>
    /// Begins a provisional target-publication batch for the active conversation turn. Abandoning it does not consume identifiers.
    /// </summary>
    public VisualTargetPublicationBatch BeginPublication()
    {
        ThrowIfDisposed();
        return new VisualTargetPublicationBatch(
            this,
            _activeTurn ?? throw new InvalidOperationException("Target publication requires an active conversation turn."),
            _publicationVersion,
            NextTargetId);
    }

    /// <summary>
    /// Resolves a retained target and promotes a historical target into the active conversation turn when one exists.
    /// </summary>
    public bool TryGetTarget(int id, [NotNullWhen(true)] out VisualTarget? target)
    {
        ThrowIfDisposed();
        if (_activeTurn?.TryGetTarget(id, out target) == true)
        {
            return true;
        }

        for (var node = _retainedTurns.Last; node is not null; node = node.Previous)
        {
            if (!node.Value.TryGetTarget(id, out target))
            {
                continue;
            }

            _activeTurn?.AddTarget(id, target);
            return true;
        }

        target = null;
        return false;
    }

    /// <summary>
    /// Evicts completed turns from oldest to newest until at most <paramref name="maximumTurnCount" /> remain.
    /// </summary>
    public void TrimRetainedTurns(int maximumTurnCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumTurnCount);
        ThrowIfDisposed();
        while (_retainedTurns.Count > maximumTurnCount)
        {
            var oldest = _retainedTurns.First ?? throw new InvalidOperationException("The retained-turn list is inconsistent.");
            _retainedTurns.RemoveFirst();
            ReleaseTurn(oldest.Value);
        }
    }

    /// <summary>
    /// Gets the Context-owned identity map for one backend-qualified identity type.
    /// </summary>
    public VisualElementIdentityMap<TIdentity> GetIdentityMap<TIdentity>(IEqualityComparer<TIdentity>? comparer = null) where TIdentity : notnull
    {
        ThrowIfDisposed();
        if (_identityMaps.TryGetValue(typeof(TIdentity), out var existing))
        {
            return (VisualElementIdentityMap<TIdentity>)existing;
        }

        var map = new VisualElementIdentityMap<TIdentity>(this, comparer);
        _identityMaps.Add(typeof(TIdentity), map);
        return map;
    }

    /// <summary>
    /// Releases every ownership batch and Agent target associated with this Context.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;

        if (_activeTurn is { } activeTurn)
        {
            _activeTurn = null;
            ReleaseTurn(activeTurn);
        }

        foreach (var turn in _retainedTurns)
        {
            ReleaseTurn(turn);
        }

        _retainedTurns.Clear();
        foreach (var retention in _retentions.ToArray())
        {
            retention.Dispose();
        }

        _retentions.Clear();
        _identityMaps.Clear();
        _targetTurnCounts.Clear();
    }

    internal void ValidateRetention(VisualElementRetention retention)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(retention.Context, this) || retention.IsDisposed || !_retentions.Contains(retention))
        {
            throw new InvalidOperationException("The visual-element retention does not belong to this Context or is no longer active.");
        }
    }

    internal void ValidateIdentityCandidate(VisualElement candidate)
    {
        ThrowIfDisposed();
        if (!candidate.IsOwnedBy(this) || candidate.HasIdentityEntry)
        {
            throw new InvalidOperationException("Only a new unretained candidate owned by this VisualContext can enter its identity map.");
        }
    }

    internal void ValidateRetainedElement(VisualElement element)
    {
        ThrowIfDisposed();
        if (!element.IsOwnedBy(this) || !element.HasIdentityEntry || element.IdentityEntry.RetainerCount <= 0)
        {
            throw new InvalidOperationException("Only a live canonical element owned by this VisualContext can be retained.");
        }
    }

    internal void Retain(VisualElementIdentityEntry entry)
    {
        ThrowIfDisposed();
        entry.RetainerCount = checked(entry.RetainerCount + 1);
    }

    internal void Release(VisualElementRetention retention, IReadOnlyCollection<VisualElementIdentityEntry> entries)
    {
        _retentions.Remove(retention);
        foreach (var entry in entries)
        {
            if (entry.RetainerCount <= 0)
            {
                throw new InvalidOperationException("The visual-element retention count is inconsistent.");
            }

            entry.RetainerCount--;
            if (entry.RetainerCount != 0)
            {
                continue;
            }

            entry.RemoveFromMap();
            entry.Element.ReleaseRetained();
        }
    }

    internal bool TryGetPublishedId(VisualTarget target, out int id)
    {
        if (_activeTurn?.TryGetTargetId(target, out id) == true)
        {
            return true;
        }

        for (var node = _retainedTurns.Last; node is not null; node = node.Previous)
        {
            if (node.Value.TryGetTargetId(target, out id))
            {
                return true;
            }
        }

        id = 0;
        return false;
    }

    internal void Commit(VisualTargetPublicationBatch batch, IReadOnlyDictionary<int, VisualTarget> targets, int newTargetCount)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(batch.Turn, _activeTurn) || batch.StartingVersion != _publicationVersion || batch.StartingTargetId != NextTargetId)
        {
            throw new InvalidOperationException(
                "The visual-target publication batch is stale because VisualContext state changed before it committed.");
        }

        foreach (var (id, target) in targets)
        {
            _activeTurn.AddTarget(id, target);
        }

        NextTargetId = checked(NextTargetId + newTargetCount);
        _publicationVersion++;
    }

    internal void CompleteTurn(VisualTargetTurn turn)
    {
        ThrowIfDisposed();
        if (!ReferenceEquals(turn, _activeTurn))
        {
            throw new InvalidOperationException("Only the active conversation turn can be completed.");
        }

        _activeTurn = null;
        if (turn.Count == 0)
        {
            ReleaseTurn(turn);
        }
        else
        {
            _retainedTurns.AddLast(turn);
            TrimToRetentionPolicy();
        }
    }

    internal void AbandonTurn(VisualTargetTurn turn)
    {
        if (!ReferenceEquals(turn, _activeTurn))
        {
            return;
        }

        _activeTurn = null;
        ReleaseTurn(turn);
    }

    internal void AddTargetReference(int id) => _targetTurnCounts[id] = _targetTurnCounts.GetValueOrDefault(id) + 1;

    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_isDisposed, this);

    private void ReleaseTurn(VisualTargetTurn turn)
    {
        foreach (var id in turn.TargetIds)
        {
            var count = _targetTurnCounts[id] - 1;
            if (count == 0)
            {
                _targetTurnCounts.Remove(id);
            }
            else
            {
                _targetTurnCounts[id] = count;
            }
        }

        turn.ReleaseRetention();
    }

    private void TrimToRetentionPolicy()
    {
        while (_retainedTurns.Count > MaximumRetainedTurnCount || (_retainedTurns.Count > 1 && _targetTurnCounts.Count > MaximumRetainedTargetCount))
        {
            var oldest = _retainedTurns.First ?? throw new InvalidOperationException("The retained-turn list is inconsistent.");
            _retainedTurns.RemoveFirst();
            ReleaseTurn(oldest.Value);
        }
    }
}

/// <summary>
/// Owns every Agent target published or looked up during one conversation turn.
/// </summary>
public sealed class VisualTargetTurn : IDisposable
{
    /// <summary>Gets the number of distinct Agent identifiers retained by this turn.</summary>
    public int Count => _targets.Count;

    internal IEnumerable<int> TargetIds => _targets.Keys;

    private readonly VisualContext _context;
    private readonly VisualElementRetention _retention;
    private readonly Dictionary<int, VisualTarget> _targets = [];
    private readonly Dictionary<string, int> _elementTargetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<VisualTarget, int> _otherTargetIds = new(ReferenceEqualityComparer.Instance);
    private bool _isCompleted;
    private bool _isReleased;

    internal VisualTargetTurn(VisualContext context, VisualElementRetention retention)
    {
        _context = context;
        _retention = retention;
    }

    /// <summary>
    /// Transfers this turn and its retained targets into the Context's historical lookup queue.
    /// </summary>
    public void Complete()
    {
        ObjectDisposedException.ThrowIf(_isReleased, this);
        if (_isCompleted)
        {
            return;
        }

        _context.CompleteTurn(this);
        _isCompleted = true;
    }

    /// <summary>
    /// Abandons an incomplete turn. A completed turn remains owned by its Context until historical eviction.
    /// </summary>
    public void Dispose()
    {
        if (_isReleased || _isCompleted)
        {
            return;
        }

        _context.AbandonTurn(this);
        _isReleased = true;
    }

    internal void AddTarget(int id, VisualTarget target)
    {
        ObjectDisposedException.ThrowIf(_isReleased, this);
        switch (target)
        {
            case ElementTarget elementTarget:
                _context.ValidateRetainedElement(elementTarget.Element);
                _retention.Retain(elementTarget.Element);
                _elementTargetIds[elementTarget.Element.Id] = id;
                break;
            case CompositeTarget compositeTarget:
                foreach (var part in compositeTarget.Parts)
                {
                    _context.ValidateRetainedElement(part.Element);
                    _retention.Retain(part.Element);
                }

                _otherTargetIds[target] = id;
                break;
            default:
                _otherTargetIds[target] = id;
                break;
        }

        if (_targets.TryAdd(id, target))
        {
            _context.AddTargetReference(id);
        }
        else
        {
            _targets[id] = target;
        }
    }

    internal bool TryGetTarget(int id, [NotNullWhen(true)] out VisualTarget? target) => _targets.TryGetValue(id, out target);

    internal bool TryGetTargetId(VisualTarget target, out int id) => target is ElementTarget elementTarget ?
        _elementTargetIds.TryGetValue(elementTarget.Element.Id, out id) :
        _otherTargetIds.TryGetValue(target, out id);

    internal void ReleaseRetention()
    {
        if (_isReleased)
        {
            return;
        }

        _isReleased = true;
        _retention.Dispose();
        _targets.Clear();
        _elementTargetIds.Clear();
        _otherTargetIds.Clear();
    }
}

/// <summary>
/// Assigns provisional target identifiers and publishes retained targets to the active conversation turn.
/// </summary>
public sealed class VisualTargetPublicationBatch
{
    /// <summary>Gets the first identifier available for a new target in this batch.</summary>
    public int StartingTargetId { get; }

    /// <summary>Gets the number of distinct targets represented by this batch.</summary>
    public int Count => _targets.Count;

    /// <summary>Gets whether this batch has committed.</summary>
    public bool IsCommitted { get; private set; }

    internal VisualTargetTurn Turn { get; }

    internal long StartingVersion { get; }

    private readonly VisualContext _context;
    private readonly Dictionary<int, VisualTarget> _targets = [];
    private readonly Dictionary<string, int> _elementIds = new(StringComparer.Ordinal);
    private readonly Dictionary<VisualTarget, int> _otherIds = new(ReferenceEqualityComparer.Instance);
    private int _newTargetCount;

    internal VisualTargetPublicationBatch(VisualContext context, VisualTargetTurn turn, long startingVersion, int startingTargetId)
    {
        _context = context;
        Turn = turn;
        StartingVersion = startingVersion;
        StartingTargetId = startingTargetId;
    }

    /// <summary>
    /// Gets an existing retained identifier or provisionally assigns the next identifier to a new target.
    /// </summary>
    public int Add(VisualTarget target)
    {
        ThrowIfCommitted();
        if (TryGetBatchId(target, out var id) || _context.TryGetPublishedId(target, out id))
        {
            _targets[id] = target;
            RememberBatchId(target, id);
            return id;
        }

        id = checked(StartingTargetId + _newTargetCount);
        _newTargetCount++;
        _targets.Add(id, target);
        RememberBatchId(target, id);
        return id;
    }

    /// <summary>
    /// Publishes every represented target into the active Agent turn.
    /// </summary>
    public void Commit()
    {
        ThrowIfCommitted();
        _context.Commit(this, _targets, _newTargetCount);
        IsCommitted = true;
    }

    private bool TryGetBatchId(VisualTarget target, out int id) => target is ElementTarget elementTarget ?
        _elementIds.TryGetValue(elementTarget.Element.Id, out id) :
        _otherIds.TryGetValue(target, out id);

    private void RememberBatchId(VisualTarget target, int id)
    {
        if (target is ElementTarget elementTarget)
        {
            _elementIds[elementTarget.Element.Id] = id;
        }
        else
        {
            _otherIds[target] = id;
        }
    }

    private void ThrowIfCommitted()
    {
        if (IsCommitted)
        {
            throw new InvalidOperationException("A committed visual-target publication batch cannot be modified or committed again.");
        }
    }
}
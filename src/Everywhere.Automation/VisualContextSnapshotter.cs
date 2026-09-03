using System.Diagnostics;
using ZLinq;

namespace Everywhere.Automation;

/// <summary>
/// Creates a bounded platform-neutral observation of a live visual-element graph.
/// </summary>
/// <remarks>
/// This is the only replacement-pipeline phase that calls platform Elements. Planning and PromptNode construction consume the returned in-memory Snapshot.
/// </remarks>
public sealed class VisualContextSnapshotter
{
    /// <summary>
    /// Creates one bounded Snapshot around the supplied core Elements.
    /// </summary>
    /// <param name="context">The identity and lifetime domain shared by every supplied Element.</param>
    /// <param name="coreElements">The ordered high-priority Elements that anchor traversal.</param>
    /// <param name="limits">The monotonic risk limits, or <see langword="null" /> to use <see cref="VisualContextSnapshotLimits.Default" />.</param>
    /// <param name="allowedTraverseDirections">The relations that traversal may observe.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>A disposable Snapshot that owns every admitted Element until publication or disposal.</returns>
    public static VisualContextSnapshot CreateSnapshot(
        VisualContext context,
        IReadOnlyList<VisualElement> coreElements,
        VisualContextSnapshotLimits? limits = null,
        VisualContextTraverseDirections allowedTraverseDirections = VisualContextTraverseDirections.All,
        CancellationToken cancellationToken = default)
    {
        var effectiveLimits = limits ?? VisualContextSnapshotLimits.Default;
        effectiveLimits.Validate();
        return new Traversal(context, coreElements, effectiveLimits, allowedTraverseDirections, cancellationToken).CreateSnapshot();
    }

    private sealed class Traversal(
        VisualContext context,
        IReadOnlyList<VisualElement> coreElements,
        VisualContextSnapshotLimits limits,
        VisualContextTraverseDirections allowedTraverseDirections,
        CancellationToken cancellationToken
    )
    {
        private readonly record struct TraverseDistance(int Global, int Local)
        {
            public static implicit operator TraverseDistance(int distance) => new(distance, distance);

            public TraverseDistance Reset() => new(Global + 1, 1);

            public TraverseDistance Step() => new(Global + 1, Local + 1);
        }

        private readonly record struct Observation(
            VisualElement Element,
            VisualElementSnapshot Snapshot,
            VisualElementFields AvailableFields,
            VisualElementFields MissingFields,
            VisualElementQueryFailureKind? FailureKind,
            string? FailureStatus
        )
        {
            public static Observation FromResult(VisualElementQueryResult result) => new(
                result.Element,
                result.Snapshot,
                result.AvailableFields,
                result.MissingFields,
                result.Failure?.Kind,
                GetFailureStatus(result.Failure?.Kind));

            public static Observation FromException(VisualElement element, Exception exception, VisualElementFields requestedFields) => new(
                element,
                default,
                VisualElementFields.None,
                requestedFields,
                GetFailureKind(exception),
                GetFailureStatus(GetFailureKind(exception)));

            private static VisualElementQueryFailureKind GetFailureKind(Exception exception) => exception switch
            {
                TimeoutException => VisualElementQueryFailureKind.Timeout,
                NotSupportedException => VisualElementQueryFailureKind.Unsupported,
                ObjectDisposedException => VisualElementQueryFailureKind.ElementUnavailable,
                _ => VisualElementQueryFailureKind.ProviderFailure,
            };

            private static string? GetFailureStatus(VisualElementQueryFailureKind? kind) => kind switch
            {
                VisualElementQueryFailureKind.Timeout => "Element query timed out.",
                VisualElementQueryFailureKind.ElementUnavailable => "Element became unavailable during query.",
                VisualElementQueryFailureKind.Unsupported => "Element query is unsupported.",
                VisualElementQueryFailureKind.ProviderFailure => "Element query failed in the platform provider.",
                _ => null,
            };
        }

        private sealed class TraversalWork : IDisposable
        {
            public Observation Observation { get; }

            public Observation? Previous { get; }

            public TraverseDistance Distance { get; }

            public VisualContextTraverseDirections Direction { get; }

            public VisualElementRelation? Relation { get; }

            public int SiblingIndex { get; }

            public string OriginElementId { get; }

            public string? DirectParentId { get; }

            public string? PendingParentAnchorId { get; }

            public float Priority { get; }

            private IVisualElementEnumerator? _enumerator;

            public TraversalWork(
                Observation observation,
                Observation? previous,
                TraverseDistance distance,
                VisualContextTraverseDirections direction,
                VisualElementRelation? relation,
                int siblingIndex,
                IVisualElementEnumerator? enumerator,
                string originElementId,
                string? directParentId,
                string? pendingParentAnchorId)
            {
                Observation = observation;
                Previous = previous;
                Distance = distance;
                Direction = direction;
                Relation = relation;
                SiblingIndex = siblingIndex;
                _enumerator = enumerator;
                OriginElementId = originElementId;
                DirectParentId = directParentId;
                PendingParentAnchorId = pendingParentAnchorId;
                Priority = GetScore();
            }

            public IVisualElementEnumerator? TakeEnumerator()
            {
                var enumerator = _enumerator;
                _enumerator = null;
                return enumerator;
            }

            public void Dispose() => _enumerator?.Dispose();

            private float GetScore()
            {
                if (Direction == VisualContextTraverseDirections.Core)
                {
                    return float.NegativeInfinity;
                }

                var score = Direction switch
                {
                    VisualContextTraverseDirections.Parent => 2000.0f,
                    VisualContextTraverseDirections.PreviousSibling => 10000.0f,
                    VisualContextTraverseDirections.NextSibling => 10000.0f,
                    VisualContextTraverseDirections.Child => 1000.0f,
                    _ => throw new ArgumentOutOfRangeException(nameof(Direction), Direction, null),
                };
                if (Distance.Local > 0)
                {
                    score /= Distance.Local;
                }

                score -= Distance.Global - Distance.Local;
                var weightedSnapshot = Direction switch
                {
                    VisualContextTraverseDirections.Parent => Observation.Snapshot,
                    VisualContextTraverseDirections.Child => Previous?.Snapshot,
                    _ => null,
                };
                if (weightedSnapshot is { } snapshot)
                {
                    score *= GetTypeWeight(snapshot.Type ?? VisualElementType.Unknown);
                }

                return -score;
            }
        }

        private readonly VisualElementQueryRequest _structuralQueryRequest = new(VisualElementFields.All & ~VisualElementFields.Text, 0);
        private readonly VisualElementRetention _retention = context.CreateRetention();
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly PriorityQueue<TraversalWork, (float Priority, long Sequence)> _queue = new();
        private readonly Dictionary<string, VisualContextSnapshotNode> _nodes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _resolvedParentAnchors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _pendingAnchorByNode = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<VisualContextSnapshotNode>> _pendingNodesByAnchor = new(StringComparer.Ordinal);
        private readonly List<string> _status = [];
        private long _nextQueueSequence;
        private long _nextTraversalOrdinal;
        private int _platformOperationCount;
        private int _providerFailureCount;
        private int _totalTextCharacters;
        private bool _isComplete = true;
        private bool _shouldStop;
        private bool _isRetentionTransferred;

        public VisualContextSnapshot CreateSnapshot()
        {
            try
            {
                EnqueueCoreElements();
                ProcessQueue();
                var roots = _nodes.Values.AsValueEnumerable().Where(static node => node.Parent is null).OrderBy(static node => node.TraversalOrdinal)
                    .ToArray();
                var snapshot = new VisualContextSnapshot(_retention, roots, _isComplete, _status.ToArray());
                _isRetentionTransferred = true;
                return snapshot;
            }
            finally
            {
                DisposeQueue();
                if (!_isRetentionTransferred)
                {
                    _retention.Dispose();
                }
            }
        }

        private void EnqueueCoreElements()
        {
            for (var index = 0; index < coreElements.Count && !_shouldStop; index++)
            {
                var element = coreElements[index];
                if (!TryBeginPlatformOperation())
                {
                    break;
                }

                Observation observation;
                try
                {
                    observation = Observation.FromResult(element.Query(_structuralQueryRequest));
                }
                catch (Exception exception) when (IsRecoverablePlatformFailure(exception))
                {
                    observation = Observation.FromException(element, exception, _structuralQueryRequest.RequestedFields);
                }

                _retention.Retain(observation.Element);
                Enqueue(
                    new TraversalWork(
                        observation,
                        null,
                        0,
                        VisualContextTraverseDirections.Core,
                        null,
                        index,
                        null,
                        observation.Element.Id,
                        null,
                        observation.Element.Id));
            }
        }

        private void ProcessQueue()
        {
            while (_queue.Count > 0 && !_shouldStop)
            {
                if (!CheckBoundary())
                {
                    break;
                }

                var work = _queue.Dequeue();
                try
                {
                    ProcessWork(work);
                }
                finally
                {
                    work.Dispose();
                }
            }
        }

        private void ProcessWork(TraversalWork work)
        {
            var id = work.Observation.Element.Id;
            var isNewNode = !_nodes.TryGetValue(id, out var node);
            var observation = work.Observation;
            if (isNewNode)
            {
                if (_nodes.Count >= limits.MaximumNodes)
                {
                    Stop("Snapshot node limit reached.");
                    return;
                }

                observation = CompleteObservation(observation);
                node = CreateNode(work, observation);
                _nodes.Add(id, node);
            }

            ApplyRelation(work, node!);
            ObserveQueryStatus(node!, observation);
            ContinueRelation(work);
            if (isNewNode && work.Observation.FailureKind is null && !_shouldStop)
            {
                ExpandNode(work, node!);
            }
        }

        private Observation CompleteObservation(Observation structuralObservation)
        {
            if (structuralObservation.FailureKind is not null)
            {
                return structuralObservation with { MissingFields = VisualElementFields.All };
            }

            var maximumTextCharacters = Math.Min(
                limits.MaximumTextCharactersPerNode,
                Math.Max(0, limits.MaximumTotalTextCharacters - _totalTextCharacters));
            if (maximumTextCharacters == 0)
            {
                AddSnapshotStatus("Snapshot text-content limit reached.");
                _isComplete = false;
                return structuralObservation with { MissingFields = structuralObservation.MissingFields | VisualElementFields.Text };
            }

            if (!TryBeginPlatformOperation())
            {
                return structuralObservation with { MissingFields = structuralObservation.MissingFields | VisualElementFields.Text };
            }

            Observation textObservation;
            var request = new VisualElementQueryRequest(VisualElementFields.Text, maximumTextCharacters);
            try
            {
                textObservation = Observation.FromResult(structuralObservation.Element.Query(request));
            }
            catch (Exception exception) when (IsRecoverablePlatformFailure(exception))
            {
                textObservation = Observation.FromException(structuralObservation.Element, exception, VisualElementFields.Text);
            }

            var availableFields = structuralObservation.AvailableFields | textObservation.AvailableFields;
            return new Observation(
                structuralObservation.Element,
                structuralObservation.Snapshot with
                {
                    TextPreview = textObservation.Snapshot.TextPreview, HasMoreText = textObservation.Snapshot.HasMoreText
                },
                availableFields,
                VisualElementFields.All & ~availableFields,
                textObservation.FailureKind,
                textObservation.FailureStatus);
        }

        private VisualContextSnapshotNode CreateNode(TraversalWork work, Observation observation)
        {
            var snapshot = observation.Snapshot;
            var status = default(string);
            if (snapshot.TextPreview is { } text)
            {
                var maximumLength = Math.Min(
                    limits.MaximumTextCharactersPerNode,
                    Math.Max(0, limits.MaximumTotalTextCharacters - _totalTextCharacters));
                if (text.Length > maximumLength)
                {
                    snapshot = snapshot with { TextPreview = text[..maximumLength], HasMoreText = true };
                    status = "Text preview was truncated by the Snapshot content limit.";
                    _isComplete = false;
                }

                _totalTextCharacters += snapshot.TextPreview?.Length ?? 0;
            }

            var node = new VisualContextSnapshotNode
            {
                Element = observation.Element,
                Snapshot = snapshot,
                AvailableFields = observation.AvailableFields,
                MissingFields = observation.MissingFields,
                LocalDistance = work.Distance.Local,
                GlobalDistance = work.Distance.Global,
                TraversalPriority = work.Priority,
                TraversalOrdinal = _nextTraversalOrdinal++,
                IsCore = work.Direction == VisualContextTraverseDirections.Core,
                IsInteractive = IsInteractive(snapshot.Type ?? VisualElementType.Unknown, snapshot.States ?? VisualElementStates.None),
            };
            if (status is not null)
            {
                node.AddStatus(status);
            }
            else if (snapshot.HasMoreText)
            {
                node.AddStatus("More text is available beyond this bounded preview.");
                _isComplete = false;
            }

            return node;
        }

        private void ApplyRelation(TraversalWork work, VisualContextSnapshotNode node)
        {
            if (work.Direction is VisualContextTraverseDirections.Child or VisualContextTraverseDirections.PreviousSibling or
                VisualContextTraverseDirections.NextSibling)
            {
                node.ObserveSiblingIndex(work.SiblingIndex);
            }

            if (work.DirectParentId is { } directParentId && _nodes.TryGetValue(directParentId, out var directParent))
            {
                Attach(directParent, node);
            }
            else if (work.PendingParentAnchorId is { } pendingParentAnchorId)
            {
                RegisterPendingParent(node, pendingParentAnchorId);
            }

            if (work.Direction != VisualContextTraverseDirections.Parent || work.Previous is not { } previous ||
                !_nodes.TryGetValue(previous.Element.Id, out var child))
            {
                return;
            }

            Attach(node, child);
            var anchorId = _pendingAnchorByNode.TryGetValue(child.Element.Id, out var pendingAnchorId) ? pendingAnchorId : child.Element.Id;
            _resolvedParentAnchors[anchorId] = node.Element.Id;
            if (!_pendingNodesByAnchor.Remove(anchorId, out var pendingNodes))
            {
                return;
            }

            foreach (var pendingNode in pendingNodes.AsValueEnumerable())
            {
                Attach(node, pendingNode);
            }
        }

        private void RegisterPendingParent(VisualContextSnapshotNode node, string anchorId)
        {
            if (node.Parent is not null)
            {
                return;
            }

            if (_resolvedParentAnchors.TryGetValue(anchorId, out var parentId) && _nodes.TryGetValue(parentId, out var parent))
            {
                Attach(parent, node);
                return;
            }

            _pendingAnchorByNode.TryAdd(node.Element.Id, anchorId);
            if (!_pendingNodesByAnchor.TryGetValue(anchorId, out var pendingNodes))
            {
                pendingNodes = [];
                _pendingNodesByAnchor.Add(anchorId, pendingNodes);
            }

            if (!pendingNodes.Contains(node))
            {
                pendingNodes.Add(node);
            }
        }

        private void Attach(VisualContextSnapshotNode parent, VisualContextSnapshotNode child)
        {
            if (!parent.TryAddChild(child))
            {
                child.AddStatus("A conflicting parent observation was ignored.");
                _isComplete = false;
            }
        }

        private void ObserveQueryStatus(VisualContextSnapshotNode node, Observation observation)
        {
            if (observation.FailureStatus is { } failureStatus)
            {
                node.AddStatus(failureStatus);
                _providerFailureCount++;
                _isComplete = false;
                if (_providerFailureCount >= limits.MaximumProviderFailures)
                {
                    Stop("Snapshot provider-failure limit reached.");
                }
            }
        }

        private void ContinueRelation(TraversalWork work)
        {
            var enumerator = work.TakeEnumerator();
            if (enumerator is null)
            {
                return;
            }

            if (work.Relation == VisualElementRelation.Child && enumerator.Index + 1 >= limits.MaximumChildrenPerNode)
            {
                enumerator.Dispose();
                AddRelationStatus(work.OriginElementId, "Child enumeration reached the per-node limit.");
                return;
            }

            if (work.Direction is
                VisualContextTraverseDirections.PreviousSibling or
                VisualContextTraverseDirections.NextSibling or
                VisualContextTraverseDirections.Child)
            {
                TryAdvanceAndEnqueue(enumerator, work);
            }
            else
            {
                enumerator.Dispose();
            }
        }

        private void ExpandNode(TraversalWork work, VisualContextSnapshotNode node)
        {
            var type = node.Snapshot.Type ?? VisualElementType.Unknown;
            switch (work.Direction)
            {
                case VisualContextTraverseDirections.Core:
                    if (type != VisualElementType.TopLevel)
                    {
                        TryCreateRelation(work, VisualElementRelation.Parent, VisualContextTraverseDirections.Parent, work.Distance.Step());
                        TryCreateRelation(
                            work,
                            VisualElementRelation.PreviousSibling,
                            VisualContextTraverseDirections.PreviousSibling,
                            work.Distance.Step());
                        TryCreateRelation(work, VisualElementRelation.NextSibling, VisualContextTraverseDirections.NextSibling, work.Distance.Step());
                    }

                    TryCreateRelation(work, VisualElementRelation.Child, VisualContextTraverseDirections.Child, work.Distance.Step());
                    break;
                case VisualContextTraverseDirections.Parent when type != VisualElementType.TopLevel:
                    TryCreateRelation(work, VisualElementRelation.Parent, VisualContextTraverseDirections.Parent, work.Distance.Step());
                    TryCreateRelation(
                        work,
                        VisualElementRelation.PreviousSibling,
                        VisualContextTraverseDirections.PreviousSibling,
                        work.Distance.Reset());
                    TryCreateRelation(work, VisualElementRelation.NextSibling, VisualContextTraverseDirections.NextSibling, work.Distance.Reset());
                    break;
                case VisualContextTraverseDirections.PreviousSibling:
                case VisualContextTraverseDirections.NextSibling:
                case VisualContextTraverseDirections.Child:
                    TryCreateRelation(work, VisualElementRelation.Child, VisualContextTraverseDirections.Child, work.Distance.Reset());
                    break;
            }
        }

        private void TryCreateRelation(
            TraversalWork previous,
            VisualElementRelation relation,
            VisualContextTraverseDirections direction,
            TraverseDistance distance)
        {
            if (_shouldStop || !allowedTraverseDirections.HasFlag(direction) || !TryBeginPlatformOperation())
            {
                return;
            }

            IVisualElementEnumerator enumerator;
            try
            {
                enumerator = previous.Observation.Element.CreateEnumerator(relation, new VisualElementEnumerationOptions(_structuralQueryRequest));
            }
            catch (Exception exception) when (IsRecoverablePlatformFailure(exception))
            {
                RecordRelationFailure(previous.Observation.Element.Id, relation, exception);
                return;
            }

            TryAdvanceAndEnqueue(enumerator, previous, relation, distance, direction);
        }

        private void TryAdvanceAndEnqueue(
            IVisualElementEnumerator enumerator,
            TraversalWork previous,
            VisualElementRelation? relation = null,
            TraverseDistance? distance = null,
            VisualContextTraverseDirections? direction = null)
        {
            if (_shouldStop || !TryBeginPlatformOperation())
            {
                enumerator.Dispose();
                return;
            }

            try
            {
                if (!enumerator.MoveNext())
                {
                    enumerator.Dispose();
                    return;
                }
            }
            catch (Exception exception) when (IsRecoverablePlatformFailure(exception))
            {
                enumerator.Dispose();
                var originElementId = relation.HasValue ? previous.Observation.Element.Id : previous.OriginElementId;
                RecordRelationFailure(
                    originElementId,
                    relation ?? previous.Relation ?? throw new InvalidOperationException("Relation work must identify its native relation."),
                    exception);
                return;
            }

            var effectiveRelation =
                relation ?? previous.Relation ?? throw new InvalidOperationException("Relation work must identify its native relation.");
            var effectiveDirection = direction ?? (effectiveRelation == VisualElementRelation.Child ?
                VisualContextTraverseDirections.NextSibling :
                previous.Direction);
            var observation = Observation.FromResult(enumerator.Current);
            _retention.Retain(observation.Element);
            var isInitialRelationItem = relation.HasValue;
            var directParentId = (effectiveDirection, isInitialRelationItem) switch
            {
                (VisualContextTraverseDirections.Child, true) => previous.Observation.Element.Id,
                (VisualContextTraverseDirections.Child, false) => previous.DirectParentId,
                (VisualContextTraverseDirections.PreviousSibling or VisualContextTraverseDirections.NextSibling, _) => previous.DirectParentId,
                _ => null,
            };
            var pendingParentAnchorId = (effectiveDirection, isInitialRelationItem) switch
            {
                (VisualContextTraverseDirections.Parent, _) => observation.Element.Id,
                (VisualContextTraverseDirections.PreviousSibling or
                    VisualContextTraverseDirections.NextSibling, true) when directParentId is null =>
                    previous.PendingParentAnchorId ?? previous.Observation.Element.Id,
                (VisualContextTraverseDirections.PreviousSibling or
                    VisualContextTraverseDirections.NextSibling, false) => previous.PendingParentAnchorId,
                _ => null,
            };
            var siblingIndex = effectiveRelation == VisualElementRelation.Child ?
                enumerator.Index :
                effectiveDirection switch
                {
                    VisualContextTraverseDirections.PreviousSibling => previous.SiblingIndex - 1,
                    VisualContextTraverseDirections.NextSibling => previous.SiblingIndex + 1,
                    _ => enumerator.Index,
                };
            var relationOriginId = isInitialRelationItem ? previous.Observation.Element.Id : previous.OriginElementId;
            var relationOrigin = isInitialRelationItem ? previous.Observation : previous.Previous;
            Enqueue(
                new TraversalWork(
                    observation,
                    relationOrigin,
                    distance ?? previous.Distance.Step(),
                    effectiveDirection,
                    effectiveRelation,
                    siblingIndex,
                    enumerator,
                    relationOriginId,
                    directParentId,
                    pendingParentAnchorId));
        }

        private void RecordRelationFailure(string originElementId, VisualElementRelation relation, Exception exception)
        {
            var status = exception switch
            {
                TimeoutException => $"{relation} enumeration timed out.",
                NotSupportedException => $"{relation} enumeration is unsupported.",
                _ => $"{relation} enumeration failed in the platform provider.",
            };
            AddRelationStatus(originElementId, status);
            _providerFailureCount++;
            if (_providerFailureCount >= limits.MaximumProviderFailures)
            {
                Stop("Snapshot provider-failure limit reached.");
            }
        }

        private void AddRelationStatus(string originElementId, string status)
        {
            if (_nodes.TryGetValue(originElementId, out var origin))
            {
                origin.AddStatus(status);
            }
            else
            {
                AddSnapshotStatus(status);
            }

            _isComplete = false;
        }

        private bool TryBeginPlatformOperation()
        {
            if (!CheckBoundary())
            {
                return false;
            }

            if (_platformOperationCount >= limits.MaximumPlatformOperations)
            {
                Stop("Snapshot platform-operation limit reached.");
                return false;
            }

            _platformOperationCount++;
            return true;
        }

        private bool CheckBoundary()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopwatch.Elapsed < limits.MaximumElapsed)
            {
                return true;
            }

            Stop("Snapshot elapsed-time limit reached.");
            return false;
        }

        private void Stop(string status)
        {
            AddSnapshotStatus(status);
            _isComplete = false;
            _shouldStop = true;
        }

        private void AddSnapshotStatus(string status)
        {
            if (!_status.Contains(status, StringComparer.Ordinal))
            {
                _status.Add(status);
            }
        }

        private void Enqueue(TraversalWork work) => _queue.Enqueue(work, (work.Priority, _nextQueueSequence++));

        private void DisposeQueue()
        {
            while (_queue.TryDequeue(out var work, out _))
            {
                work.Dispose();
            }
        }

        private static float GetTypeWeight(VisualElementType type) => type switch
        {
            VisualElementType.Label or VisualElementType.TextEdit or VisualElementType.Document => 2.0f,
            VisualElementType.Panel or VisualElementType.TopLevel or VisualElementType.TabControl => 1.5f,
            VisualElementType.Button or
                VisualElementType.ComboBox or
                VisualElementType.CheckBox or
                VisualElementType.RadioButton or
                VisualElementType.Slider or
                VisualElementType.MenuItem or
                VisualElementType.TabItem => 1.0f,
            VisualElementType.Image or VisualElementType.ScrollBar => 0.5f,
            _ => 1.0f,
        };

        private static bool IsRecoverablePlatformFailure(Exception exception) =>
            exception is TimeoutException or NotSupportedException or InvalidOperationException;

        private static bool IsInteractive(VisualElementType type, VisualElementStates states)
        {
            if (type is VisualElementType.Button or VisualElementType.Hyperlink or VisualElementType.CheckBox or VisualElementType.RadioButton or
                VisualElementType.ComboBox or VisualElementType.ListView or VisualElementType.ListViewItem or VisualElementType.TreeView or
                VisualElementType.TreeViewItem or VisualElementType.DataGrid or VisualElementType.DataGridItem or VisualElementType.TabControl or
                VisualElementType.TabItem or VisualElementType.Menu or VisualElementType.MenuItem or VisualElementType.Slider or
                VisualElementType.ScrollBar or VisualElementType.TextEdit or VisualElementType.Table or VisualElementType.TableRow)
            {
                return true;
            }

            return (states & (VisualElementStates.Focused | VisualElementStates.Selected)) != 0;
        }
    }
}
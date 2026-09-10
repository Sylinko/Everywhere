using System.Collections;
using Everywhere.Chat;

namespace Everywhere.Automation.Tests;

/// <summary>Exercises malformed provider relations independently of the declarative UI scenario tree.</summary>
public sealed class AdversarialSnapshotTests
{
    [Test]
    public void Snapshot_WhenRelationsContainCyclesAndSharedChildren_PreservesUsefulContentAndReleasesOwners()
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var nodes = CreateNodes(context, acquisition);
        nodes[0].Children = [nodes[1], nodes[2]];
        nodes[1].Children = [nodes[1], nodes[3], nodes[0]];
        nodes[2].Children = [nodes[3]];
        using var turn = context.BeginTurn();
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [nodes[0]], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        var prompt = VisualContextPromptBuilder.Build(context, snapshot).ToString();
        var usefulTarget = Enumerable.Range(1, context.NextTargetId - 1).Single(id => context.TryGetTarget(id, out var target) && target is ElementTarget element && ReferenceEquals(element.Element, nodes[3]));
        acquisition.Dispose();
        snapshot.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(observed.Select(node => node.Element.Id).Distinct().Count(), Is.EqualTo(4));
            Assert.That(observed, Has.Length.EqualTo(4));
            Assert.That(nodes.Select(node => node.EnumerationCount), Is.All.EqualTo(1));
            Assert.That(nodes.Select(node => node.EnumeratorDisposalCount), Is.All.EqualTo(1));
            Assert.That(prompt, Does.Contain("Useful content").And.Contain("conflicting parent"));
            Assert.That(prompt, Does.Not.Contain("Snapshot observation is incomplete"));
            Assert.That(context.TryGetTarget(usefulTarget, out var target), Is.True);
            if (target is not ElementTarget elementTarget) throw new InvalidOperationException("Expected a retained element target.");
            Assert.That(elementTarget.Element.ReadText().Text, Is.EqualTo("Useful content"));
        });
        turn.Complete();
        context.TrimRetainedTurns(0);
        Assert.That(nodes.Select(node => node.ReleaseCount), Is.All.EqualTo(1));
    }

    [TestCase(24, 1000, "Snapshot platform-operation limit reached")]
    [TestCase(1000, 12, "Child enumeration reached the per-node limit")]
    public void Snapshot_WhenEnumeratorRepeatsForever_StopsAtBudgetAndDoesNotReexpand(int operationLimit, int childLimit, string expectedStatus)
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var nodes = CreateNodes(context, acquisition);
        nodes[0].Children = Repeat(nodes[3]);
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(context, [nodes[0]], new VisualContextSnapshotLimits { MaximumPlatformOperations = operationLimit, MaximumChildrenPerNode = childLimit }, VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Length.EqualTo(2));
            Assert.That(snapshot.Status.Concat(observed.SelectMany(node => node.Status)), Does.Contain(expectedStatus));
            Assert.That(nodes[0].MoveCount, Is.LessThanOrEqualTo(Math.Min(operationLimit, childLimit)));
            Assert.That(nodes[3].EnumerationCount, Is.EqualTo(1));
            Assert.That(nodes[0].EnumeratorDisposalCount, Is.EqualTo(1));
            Assert.That(nodes[3].EnumeratorDisposalCount, Is.EqualTo(1));
        });
        acquisition.Dispose();
        snapshot.Dispose();
        Assert.That(nodes.Select(node => node.ReleaseCount), Is.All.EqualTo(1));
    }

    [TestCase(VisualElementQueryFailureKind.Unsupported)]
    public void Snapshot_WhenRecoverableElementFailuresRepeat_DoesNotConsumeProviderFailureBudget(VisualElementQueryFailureKind failureKind)
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var nodes = Enumerable.Range(0, 21)
            .Select(index => context.GetIdentityMap<int>().GetOrAdd(acquisition, index, index, static (identity, id) => new GraphElement(identity, id)))
            .ToArray();
        nodes[0].Children = nodes[1..];
        foreach (var node in nodes[1..]) node.QueryFailureKind = failureKind;

        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            context,
            [nodes[0]],
            new VisualContextSnapshotLimits { MaximumProviderFailures = 1 },
            VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Length.EqualTo(nodes.Length));
            Assert.That(snapshot.Status, Does.Not.Contain("Snapshot provider-failure limit reached"));
            Assert.That(observed.Skip(1).SelectMany(node => node.Status).Count(), Is.EqualTo(nodes.Length - 1));
            Assert.That(observed, Has.All.Matches<VisualContextSnapshotNode>(node => (node.AvailableFields & node.MissingFields) == 0));
        });
    }

    [Test]
    public void Snapshot_WhenEnumeratedElementsAreAlreadyUnavailable_SkipsThemAndContinuesEnumeration()
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var nodes = Enumerable.Range(0, 5)
            .Select(index => context.GetIdentityMap<int>().GetOrAdd(acquisition, index, index, static (identity, id) => new GraphElement(identity, id)))
            .ToArray();
        nodes[0].Children = nodes[1..];
        nodes[1].QueryFailureKind = VisualElementQueryFailureKind.ElementUnavailable;
        nodes[3].QueryFailureKind = VisualElementQueryFailureKind.ElementUnavailable;

        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            context,
            [nodes[0]],
            new VisualContextSnapshotLimits { MaximumProviderFailures = 1 },
            VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(observed.Select(node => node.Element.Id), Is.EqualTo(new[] { "0", "2", "4" }));
            Assert.That(snapshot.Status, Does.Not.Contain("Snapshot provider-failure limit reached"));
            Assert.That(observed.SelectMany(node => node.Status), Does.Not.Contain("Element became unavailable during query"));
            Assert.That(nodes[0].MoveCount, Is.EqualTo(nodes.Length));
            Assert.That(nodes[0].EnumeratorDisposalCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void Snapshot_WhenCoreElementIsUnavailable_RetainsItsFailureSkeleton()
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var core = context.GetIdentityMap<int>().GetOrAdd(acquisition, 0, 0, static (identity, id) => new GraphElement(identity, id));
        core.QueryFailureKind = VisualElementQueryFailureKind.ElementUnavailable;

        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            context,
            [core],
            allowedTraverseDirections: VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Length.EqualTo(1));
            Assert.That(observed[0].Element, Is.SameAs(core));
            Assert.That(observed[0].Status, Does.Contain("Element became unavailable during query"));
        });
    }

    [Test]
    public void Snapshot_WhenProviderFailuresRepeat_StopsAtProviderFailureBudget()
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var nodes = Enumerable.Range(0, 5)
            .Select(index => context.GetIdentityMap<int>().GetOrAdd(acquisition, index, index, static (identity, id) => new GraphElement(identity, id)))
            .ToArray();
        nodes[0].Children = nodes[1..];
        foreach (var node in nodes[1..]) node.TextFailureKind = VisualElementQueryFailureKind.ProviderFailure;

        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            context,
            [nodes[0]],
            new VisualContextSnapshotLimits { MaximumProviderFailures = 2 },
            VisualContextTraverseDirections.Child);
        var observed = Flatten(snapshot.Roots).ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(observed, Has.Length.EqualTo(3));
            Assert.That(snapshot.Status, Does.Contain("Snapshot provider-failure limit reached"));
            Assert.That(observed.SelectMany(node => node.Status).Count(status => status == "Element query failed in the platform provider"), Is.EqualTo(2));
        });
    }

    private static GraphElement[] CreateNodes(VisualContext context, VisualElementRetention retention) => Enumerable.Range(0, 4)
        .Select(index => context.GetIdentityMap<int>().GetOrAdd(retention, index, index, static (identity, id) => new GraphElement(identity, id))).ToArray();

    private static IEnumerable<GraphElement> Repeat(GraphElement element)
    {
        while (true) yield return element;
    }

    private static IEnumerable<VisualContextSnapshotNode> Flatten(IReadOnlyList<VisualContextSnapshotNode> roots)
    {
        foreach (var root in roots)
        {
            yield return root;
            foreach (var child in Flatten(root.Children)) yield return child;
        }
    }

    private sealed class GraphElement(VisualElementIdentity identity, int id) : VisualElement(identity, id.ToString())
    {
        public IEnumerable<GraphElement> Children { get; set; } = [];
        public int EnumerationCount { get; private set; }
        public int EnumeratorDisposalCount { get; private set; }
        public int MoveCount { get; private set; }
        public int ReleaseCount { get; private set; }

        public VisualElementQueryFailureKind? QueryFailureKind { get; set; }

        public VisualElementQueryFailureKind? TextFailureKind { get; set; }

        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request)
        {
            if (QueryFailureKind is not { } failureKind)
            {
                return new VisualElementQueryResult(this,
                    new VisualElementSnapshot(Id, id == 3 ? VisualElementType.Label : VisualElementType.Panel, null, null, null, false, null, null, null), request.RequestedFields, VisualElementFields.None, null);
            }

            var availableFields = request.RequestedFields & VisualElementFields.Id;
            return new VisualElementQueryResult(
                this,
                new VisualElementSnapshot(availableFields == VisualElementFields.Id ? Id : null, null, null, null, null, false, null, null, null),
                availableFields,
                request.RequestedFields & ~availableFields,
                new VisualElementQueryFailure(failureKind, null));
        }

        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters) => TextFailureKind is { } failureKind ?
            VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(failureKind, null)) :
            VisualElementTextReadResult.FromSuccess(id == 3 ? "Useful content" : string.Empty, offset, maxCharacters);
        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        protected override void ReleaseCore() => ReleaseCount++;
        protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request)
        {
            Assert.That(relation, Is.EqualTo(VisualElementRelation.Child));
            EnumerationCount++;
            return new GraphEnumerator(this, Children.GetEnumerator(), request);
        }

        private sealed class GraphEnumerator(GraphElement origin, IEnumerator<GraphElement> items, VisualElementQueryRequest request) : IVisualElementEnumerator
        {
            public VisualElementQueryResult Current => items.Current.Query(request);
            object IEnumerator.Current => Current;
            public int Count => -1;
            public int Index { get; private set; } = -1;
            public bool HasMore => throw new NotSupportedException();
            public bool MoveNext() { origin.MoveCount++; Index++; return items.MoveNext(); }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() { origin.EnumeratorDisposalCount++; items.Dispose(); }
        }
    }
}

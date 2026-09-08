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

    [TestCase(24, 1000, "Snapshot platform-operation limit reached.")]
    [TestCase(1000, 12, "Child enumeration reached the per-node limit.")]
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

    private static GraphElement[] CreateNodes(VisualContext context, VisualElementRetention retention) => Enumerable.Range(0, 4)
        .Select(index => context.GetIdentityMap<int>().GetOrAdd(retention, index, context, static (id, owner) => new GraphElement(owner, id))).ToArray();

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

    private sealed class GraphElement(VisualContext context, int id) : VisualElement(context, id.ToString())
    {
        public IEnumerable<GraphElement> Children { get; set; } = [];
        public int EnumerationCount { get; private set; }
        public int EnumeratorDisposalCount { get; private set; }
        public int MoveCount { get; private set; }
        public int ReleaseCount { get; private set; }

        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => new(this,
            new VisualElementSnapshot(Id, id == 3 ? VisualElementType.Label : VisualElementType.Panel, null, null, null, false, null, null, null), request.RequestedFields, VisualElementFields.None, null);
        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters) => VisualElementTextReadResult.FromSuccess(id == 3 ? "Useful content" : string.Empty, offset, maxCharacters);
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

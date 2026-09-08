using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;

namespace Everywhere.Automation.Tests;

public sealed class VisualContextSnapshotterTests
{
    [Test]
    public void CreateSnapshot_WhenCoreSiblingsObserveSameParent_CoalescesOneRootAndPreservesBothEdges()
    {
        using var backend = CreateBackend(new Panel(new Text("first"), new Text("middle"), new Text("last")));
        var first = backend.GetElement(0);
        var last = backend.GetElement(2);
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [first, last], allowedTraverseDirections: VisualContextTraverseDirections.Parent);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Status, Is.Empty);
            Assert.That(snapshot.Roots, Has.Count.EqualTo(1));
            Assert.That(snapshot.Roots[0].Element, Is.SameAs(backend.RootElement));
            Assert.That(snapshot.Roots[0].Children.Select(static child => child.Element), Is.EqualTo(new[] { first, last }));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(backend.Operations.EnumeratorCreatedCount));
        });
    }

    [Test]
    public void CreateSnapshot_WhenCollectionIsHuge_StopsAtPerNodeChildLimitWithoutEagerMaterialization()
    {
        var generatedItems = 0;
        var scenario = Scenario.Define("huge-snapshot", context => new VirtualList(context, "items", 100_000, (_, index) =>
        {
            generatedItems++;
            return new Text($"item-{index}");
        }));
        using var backend = new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
        var limits = new VisualContextSnapshotLimits
        {
            MaximumChildrenPerNode = 5,
            MaximumNodes = 32,
            MaximumPlatformOperations = 128,
        };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], limits, VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var prompt = VisualContextPromptBuilder.Build(backend.Context, snapshot).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Roots, Has.Count.EqualTo(1));
            Assert.That(snapshot.Roots[0].Children, Has.Count.EqualTo(5));
            Assert.That(snapshot.Roots[0].Status, Does.Contain("Child enumeration reached the per-node limit."));
            Assert.That(snapshot.Status, Is.Empty);
            Assert.That(prompt, Does.Contain("Child enumeration reached the per-node limit."));
            Assert.That(prompt, Does.Not.Contain("Snapshot observation is incomplete."));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(6));
            Assert.That(generatedItems, Is.LessThan(100));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(backend.Operations.EnumeratorCreatedCount));
        });
    }

    [Test]
    public void CreateSnapshot_WhenTextHasContinuation_PreservesSuccessfulBoundedObservation()
    {
        using var backend = CreateBackend(new Text(new string('x', 100)));
        var limits = new VisualContextSnapshotLimits
        {
            MaximumTextCharactersPerNode = 10,
            MaximumTotalTextCharacters = 10,
        };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], limits, VisualContextTraverseDirections.Core);
        var root = snapshot.Roots[0];

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Status, Is.Empty);
            Assert.That(root.Snapshot.TextPreview, Has.Length.EqualTo(10));
            Assert.That(root.Snapshot.HasMoreText, Is.True);
            Assert.That(root.Status, Is.Empty);
        });
    }

    [Test]
    public void CreateSnapshot_WhenPlatformOperationLimitIsReached_ReturnsPartialSnapshotAndDisposesEnumerators()
    {
        using var backend = CreateBackend(new Panel(new Text("first"), new Text("second")));
        var limits = new VisualContextSnapshotLimits { MaximumPlatformOperations = 3 };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], limits, VisualContextTraverseDirections.All);

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Status, Does.Contain("Snapshot platform-operation limit reached."));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(backend.Operations.EnumeratorCreatedCount));
        });
    }

    [Test]
    public void CreateSnapshot_WhenVisibleAndOffscreenCandidatesCompete_PrioritizesVisibleCandidate()
    {
        using var backend = CreateBackend(
            new Panel(
                new Panel(new Text("offscreen") { States = ScenarioControlStates.Offscreen }),
                new Panel(new Text("visible"))));
        var firstPanel = backend.GetElement(0);
        var secondPanel = backend.GetElement(1);
        var visible = backend.GetElement(1, 0);
        var limits = new VisualContextSnapshotLimits { MaximumNodes = 3 };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [firstPanel, secondPanel], limits, VisualContextTraverseDirections.Child);

        Assert.That(Flatten(snapshot).Select(static node => node.Element), Does.Contain(visible));
    }

    [Test]
    public void CreateSnapshot_WhenVisibleAndOffscreenCandidatesFit_RetainsBothCandidates()
    {
        using var backend = CreateBackend(
            new Panel(
                new Panel(new Text("offscreen") { States = ScenarioControlStates.Offscreen }),
                new Panel(new Text("visible"))));
        var firstPanel = backend.GetElement(0);
        var secondPanel = backend.GetElement(1);
        var offscreen = backend.GetElement(0, 0);
        var visible = backend.GetElement(1, 0);
        var limits = new VisualContextSnapshotLimits { MaximumNodes = 4 };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [firstPanel, secondPanel], limits, VisualContextTraverseDirections.Child);
        var retainedElements = Flatten(snapshot).Select(static node => node.Element).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(retainedElements, Does.Contain(visible));
            Assert.That(retainedElements, Does.Contain(offscreen));
        });
    }

    [Test]
    public void CreateSnapshot_WhenEveryCandidateIsOffscreen_ContinuesTraversal()
    {
        using var backend = CreateBackend(new Panel(new Text("first") { States = ScenarioControlStates.Offscreen }, new Text("second") { States = ScenarioControlStates.Offscreen }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);

        Assert.That(Flatten(snapshot), Has.Count.EqualTo(3));
    }

    [Test]
    public void CreateSnapshot_WhenCoreElementIsOffscreen_KeepsCorePriority()
    {
        using var backend = CreateBackend(new Panel(new Text("visible")) { States = ScenarioControlStates.Offscreen });
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        var nodes = Flatten(snapshot);

        Assert.Multiple(() =>
        {
            Assert.That(nodes[0].Element, Is.SameAs(backend.RootElement));
            Assert.That(nodes[0].IsCore, Is.True);
            Assert.That(nodes[0].TraversalPriority, Is.EqualTo(float.NegativeInfinity));
        });
    }

    private static ScenarioMockBackend CreateBackend(VisualControl root)
    {
        var scenario = Scenario.Define("snapshot", _ => root);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }

    private static IReadOnlyList<VisualContextSnapshotNode> Flatten(VisualContextSnapshot snapshot)
    {
        var nodes = new List<VisualContextSnapshotNode>();
        foreach (var root in snapshot.Roots)
        {
            Add(root);
        }

        return nodes;

        void Add(VisualContextSnapshotNode node)
        {
            nodes.Add(node);
            foreach (var child in node.Children)
            {
                Add(child);
            }
        }
    }
}

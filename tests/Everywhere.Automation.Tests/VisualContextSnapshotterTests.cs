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
            Assert.That(snapshot.IsComplete, Is.True);
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

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.IsComplete, Is.False);
            Assert.That(snapshot.Roots, Has.Count.EqualTo(1));
            Assert.That(snapshot.Roots[0].Children, Has.Count.EqualTo(5));
            Assert.That(snapshot.Roots[0].Status, Does.Contain("Child enumeration reached the per-node limit."));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(6));
            Assert.That(generatedItems, Is.LessThan(100));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(backend.Operations.EnumeratorCreatedCount));
        });
    }

    [Test]
    public void CreateSnapshot_WhenTextExceedsPreviewLimit_PreservesContinuationFact()
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
            Assert.That(snapshot.IsComplete, Is.False);
            Assert.That(root.Snapshot.TextPreview, Has.Length.EqualTo(10));
            Assert.That(root.Snapshot.HasMoreText, Is.True);
            Assert.That(root.Status, Does.Contain("More text is available beyond this bounded preview."));
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
            Assert.That(snapshot.IsComplete, Is.False);
            Assert.That(snapshot.Status, Does.Contain("Snapshot platform-operation limit reached."));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(backend.Operations.EnumeratorCreatedCount));
        });
    }

    private static ScenarioMockBackend CreateBackend(VisualControl root)
    {
        var scenario = Scenario.Define("snapshot", _ => root);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }
}

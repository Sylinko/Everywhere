using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Everywhere.Chat;

namespace Everywhere.Automation.Tests;

public sealed class VisualQueryTests
{
    [Test]
    public void Execute_WhenTargetIsElement_UsesCanonicalSnapshotAndPromptPipeline()
    {
        using var backend = CreateBackend(new Window(new Panel(new Button("Save"), new TextBox("Draft"))));
        using var turn = backend.Context.BeginTurn();
        var target = new ElementTarget { Element = backend.RootElement };
        var request = new VisualQueryRequest { Directions = VisualContextTraverseDirections.Child, Limit = 16 };

        var rendered = VisualQuery.Execute(backend.Context, target, request, VisualContextPromptOptions.Default).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("Save").And.Contain("Draft"));
            Assert.That(turn.Count, Is.GreaterThan(0));
            Assert.That(backend.Operations.ScalarQueryCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Execute_WhenCompositeIsPaged_ExpandsSelectedObservedMembersWithoutPretendingTheyAreOneElement()
    {
        using var backend = CreateBackend(new Window(new Panel(new Text("first fragment"), new Text("second fragment"))));
        var first = backend.GetElement(0, 0);
        var second = backend.GetElement(0, 1);
        var target = new CompositeTarget
        {
            Parts =
            [
                new CompositePart { Element = first, Snapshot = first.Query(VisualElementQueryRequest.Default).Snapshot },
                new CompositePart { Element = second, Snapshot = second.Query(VisualElementQueryRequest.Default).Snapshot },
            ],
            Preview = "first fragment\nsecond fragment",
        };
        using var turn = backend.Context.BeginTurn();
        var request = new VisualQueryRequest { Directions = VisualContextTraverseDirections.Core, Offset = 1, Limit = 1 };

        var rendered = VisualQuery.Execute(backend.Context, target, request, VisualContextPromptOptions.Default).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("first fragment").And.Not.Contain("second fragment"));
            Assert.That(rendered, Does.Contain("continue with offset 2"));
            Assert.That(rendered, Does.Not.Contain("<Composite"));
            Assert.That(turn.Count, Is.EqualTo(1));
        });
    }

    [Test]
    public void Execute_WhenElementOffsetExceedsAnchor_ReturnsActionableStatusWithoutPublishingTargets()
    {
        using var backend = CreateBackend(new Window(new Button("Save")));
        using var turn = backend.Context.BeginTurn();
        var target = new ElementTarget { Element = backend.RootElement };
        var request = new VisualQueryRequest { Offset = 2 };

        var rendered = VisualQuery.Execute(backend.Context, target, request, VisualContextPromptOptions.Default).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("Query a returned child ID instead."));
            Assert.That(turn.Count, Is.Zero);
        });
    }

    private static ScenarioMockBackend CreateBackend(VisualControl root)
    {
        var scenario = Scenario.Define("visual-query", _ => root);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }
}

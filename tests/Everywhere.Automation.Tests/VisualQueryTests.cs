using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Everywhere.Chat;

namespace Everywhere.Automation.Tests;

public sealed class VisualQueryTests
{
    [Test]
    public async Task Execute_WhenTargetIsElement_UsesCanonicalSnapshotAndPromptPipeline()
    {
        using var backend = CreateBackend(new Window(new Panel(new Button("Save"), new TextBox("Draft"))));
        using var turn = backend.Context.BeginTurn();
        var target = new ElementTarget { Element = backend.RootElement };
        var request = new VisualQueryRequest { Directions = VisualContextTraverseDirections.Child, Limit = 16 };

        var rendered = (await new VisualQuery(backend.Context).ExecuteAsync(target, request, VisualContextPromptOptions.Default)).Content;

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("Save").And.Contain("Draft"));
            Assert.That(turn.Count, Is.GreaterThan(0));
            Assert.That(backend.Operations.ScalarQueryCount, Is.GreaterThan(0));
        });
    }

    [Test]
    public void Execute_WhenTargetIsComposite_ReportsUnsupportedStructuralQuery()
    {
        using var backend = CreateBackend(new Window(new Panel(new Text("first fragment"), new Text("second fragment"))));
        var first = backend.GetElement(0, 0);
        var second = backend.GetElement(0, 1);
        var target = new CompositeTarget
        {
            Parts =
            [
                new CompositePart { Element = first, ContentSource = CompositePartContentSource.Text },
                new CompositePart { Element = second, ContentSource = CompositePartContentSource.Text },
            ],
        };
        using var turn = backend.Context.BeginTurn();
        var request = new VisualQueryRequest { Directions = VisualContextTraverseDirections.Core };

        var exception = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await new VisualQuery(backend.Context).ExecuteAsync(target, request, VisualContextPromptOptions.Default));

        Assert.Multiple(() =>
        {
            Assert.That(exception!.Message, Does.Contain("does not expose structural relations"));
            Assert.That(turn.Count, Is.Zero);
        });
    }

    [Test]
    public async Task Execute_WhenOffsetIsAppliedToSiblingRelations_SkipsEachDirectionIndependently()
    {
        using var backend = CreateBackend(
            new Panel(
                new Text("far left"),
                new Text("near left"),
                new Text("center"),
                new Text("near right"),
                new Text("far right")));
        using var turn = backend.Context.BeginTurn();
        var target = new ElementTarget { Element = backend.GetElement(2) };
        var request = new VisualQueryRequest
        {
            Directions = VisualContextTraverseDirections.PreviousSibling | VisualContextTraverseDirections.NextSibling,
            Offset = 1,
        };

        var rendered = (await new VisualQuery(backend.Context).ExecuteAsync(target, request, VisualContextPromptOptions.Default)).Content;

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("far left").And.Contain("far right").And.Contain("center"));
            Assert.That(rendered, Does.Not.Contain("near left").And.Not.Contain("near right"));
            Assert.That(turn.Count, Is.GreaterThan(0));
        });
    }

    [Test]
    public async Task Execute_WhenInitialRelationUsesOffset_RecursiveRelationsRestartAtZero()
    {
        using var backend = CreateBackend(
            new Panel(
                new Panel(new Text("skipped branch")),
                new Panel(new Text("first recursive child"), new Text("second recursive child"))));
        using var turn = backend.Context.BeginTurn();
        var target = new ElementTarget { Element = backend.RootElement };
        var request = new VisualQueryRequest { Directions = VisualContextTraverseDirections.Child, Offset = 1 };

        var rendered = (await new VisualQuery(backend.Context).ExecuteAsync(target, request, VisualContextPromptOptions.Default)).Content;

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Not.Contain("skipped branch"));
            Assert.That(rendered, Does.Contain("first recursive child").And.Contain("second recursive child"));
            Assert.That(turn.Count, Is.GreaterThan(0));
        });
    }

    private static ScenarioMockBackend CreateBackend(VisualControl root)
    {
        var scenario = Scenario.Define("visual-query", _ => root);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }
}

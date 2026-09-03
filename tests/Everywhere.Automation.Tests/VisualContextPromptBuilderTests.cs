using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Everywhere.Chat;

namespace Everywhere.Automation.Tests;

public sealed class VisualContextPromptBuilderTests
{
    [Test]
    public void Build_WhenPassiveLabelsAreAdjacent_PublishesQueryableCompositeWithoutAdditionalPlatformReads()
    {
        using var backend = CreateBackend(new Window(new Panel(new Text("first fragment"), new Text("second fragment"), new Button("Continue"))));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        var scalarQueryCount = backend.Operations.ScalarQueryCount;
        var moveNextAttemptCount = backend.Operations.MoveNextAttemptCount;
        using var turn = backend.Context.BeginTurn();

        var prompt = VisualContextPromptBuilder.Build(backend.Context, snapshot);
        var rendered = prompt.ToString();
        var composite = GetTargets(backend.Context).OfType<CompositeTarget>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("<Composite"));
            Assert.That(rendered, Does.Contain("first fragment"));
            Assert.That(rendered, Does.Contain("second fragment"));
            Assert.That(rendered, Does.Contain("observedMembers=2"));
            Assert.That(composite.Parts, Has.Count.EqualTo(2));
            Assert.That(rendered, Does.Not.Contain("capabilities="));
            Assert.That(backend.Operations.ScalarQueryCount, Is.EqualTo(scalarQueryCount));
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.EqualTo(moveNextAttemptCount));
        });
    }

    [Test]
    public void Build_WhenElementHasSalientStates_EmitsSparseCompactFlags()
    {
        using var backend = CreateBackend(new Window(new Button("Send") { States = ScenarioControlStates.Disabled | ScenarioControlStates.Focused }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();

        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("<Button").And.Contain(" disabled focused"));
            Assert.That(rendered, Does.Not.Contain("states=").And.Not.Contain("complete=").And.Not.Contain("important=").And.Not.Contain("capabilities="));
        });
    }

    [Test]
    public void Build_WhenPromptBudgetPrunesTargets_CommitsOnlyIdsRepresentedByFinalPrompt()
    {
        var children = Enumerable.Range(0, 30).Select(index => (VisualControl)new Button($"Action {index:00}")).ToArray();
        using var backend = CreateBackend(new Window(children));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var options = new VisualContextPromptOptions { TargetTokenBudget = 100 };

        var prompt = VisualContextPromptBuilder.Build(backend.Context, snapshot, options);
        var rendered = prompt.ToString();
        var targets = GetTargets(backend.Context).ToArray();

        Assert.Multiple(() =>
        {
            Assert.That(targets.Length, Is.GreaterThan(0).And.LessThan(children.Length + 1));
            Assert.That(backend.Context.NextTargetId, Is.EqualTo(targets.Length + 1));
            Assert.That(rendered, Does.Contain("Some visual targets were omitted by the prompt budget."));
            for (var id = 1; id < backend.Context.NextTargetId; id++) Assert.That(rendered, Does.Contain($"id={id}"));
        });
    }

    [Test]
    public void Build_WhenRequiredSkeletonCannotFit_AbandonsPublicationWithoutConsumingId()
    {
        using var backend = CreateBackend(new Window(new Button("Required action")));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var options = new VisualContextPromptOptions { TargetTokenBudget = 1 };

        Assert.That(() => VisualContextPromptBuilder.Build(backend.Context, snapshot, options), Throws.TypeOf<Prompting.Documents.PromptBudgetExceededException>());
        Assert.Multiple(() =>
        {
            Assert.That(backend.Context.NextTargetId, Is.EqualTo(1));
            Assert.That(backend.Context.TargetCount, Is.Zero);
        });
    }

    [Test]
    public void Build_WhenMultipleRootsCompeteForBudget_AdmitsUsefulContentFromEachRoot()
    {
        var firstRootChildren = Enumerable.Range(0, 30).Select(index => (VisualControl)new Button($"First root action {index:00}")).ToArray();
        using var backend = CreateBackend(new Window(firstRootChildren), new Window(new Button("Second root action")));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, backend.RootElements, allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var options = new VisualContextPromptOptions { TargetTokenBudget = 100 };

        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, options).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("First root action"));
            Assert.That(rendered, Does.Contain("Second root action"));
            Assert.That(rendered, Does.Contain("Some visual targets were omitted by the prompt budget."));
        });
    }

    private static ScenarioMockBackend CreateBackend(params IReadOnlyList<VisualControl> roots)
    {
        var scenario = Scenario.DefineRoots("prompt-builder", _ => roots);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }

    private static IEnumerable<VisualTarget> GetTargets(VisualContext context)
    {
        for (var id = 1; id < context.NextTargetId; id++)
        {
            if (context.TryGetTarget(id, out var target)) yield return target;
        }
    }
}

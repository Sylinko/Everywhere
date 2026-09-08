using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Everywhere.Chat;

namespace Everywhere.Automation.Tests;

public sealed class VisualContextPromptBuilderTests
{
    [Test]
    public void Build_WhenRootsHaveDifferentBodyCounts_SharesBudgetAcrossRootsAndWithinEachRoot()
    {
        var text = string.Concat(Enumerable.Repeat("long content ", 200));
        using var backend = CreateBackend(new Window(new TextBox(text) { Name = "first" }, new TextBox(text) { Name = "second" }), new Window(new TextBox(text) { Name = "third" }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, backend.RootElements, allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 400 });
        var bodies = System.Text.RegularExpressions.Regex.Matches(rendered, @"<TextEdit\b[^>]*>([^<]*)</TextEdit>");
        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(3));
            Assert.That(bodies[0].Groups[1].Value.Length, Is.GreaterThan(0));
            Assert.That(bodies[1].Groups[1].Value.Length, Is.GreaterThan(0));
            Assert.That(bodies[2].Groups[1].Value.Length, Is.GreaterThan(bodies[0].Groups[1].Value.Length));
            Assert.That(Everywhere.Prompting.TokenHelper.EstimateTokenCount(rendered), Is.LessThanOrEqualTo(400));
        });
    }

    [Test]
    public void Build_WhenCompositeCompetesWithElement_GivesBothContinuousPreviews()
    {
        var text = string.Concat(Enumerable.Repeat("fragment ", 200));
        using var backend = CreateBackend(new Window(new Text(text), new Text("tail"), new TextBox(text) { Name = "editor" }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 200 });
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("moreText>fragment"));
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(rendered, @"<Composite\b[^>]*>fragment"), Is.True);
            Assert.That(System.Text.RegularExpressions.Regex.IsMatch(rendered, @"<TextEdit\b[^>]*>fragment"), Is.True);
            Assert.That(Everywhere.Prompting.TokenHelper.EstimateTokenCount(rendered), Is.LessThanOrEqualTo(200));
        });
    }

    [Test]
    public void Build_WhenLongBodiesCompete_GivesEachBodyAPrefixWithinBudget()
    {
        var text = string.Concat(Enumerable.Repeat("中文😀<&> long content ", 160));
        using var backend = CreateBackend(new Window(new TextBox(text) { Name = "first" }, new TextBox(text) { Name = "second" }, new TextBox(text) { Name = "third" }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var queryCount = backend.Operations.ScalarQueryCount;
        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 300 });
        var bodies = System.Text.RegularExpressions.Regex.Matches(rendered, @"<TextEdit\b[^>]*>([^<]*)</TextEdit>");
        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(3));
            foreach (var body in bodies.Cast<System.Text.RegularExpressions.Match>())
            {
                var decoded = System.Net.WebUtility.HtmlDecode(body.Groups[1].Value);
                Assert.That(decoded, Is.Not.Empty);
                Assert.That(text, Does.StartWith(decoded));
                Assert.That(char.IsHighSurrogate(decoded[^1]), Is.False);
            }
            Assert.That(Everywhere.Prompting.TokenHelper.EstimateTokenCount(rendered), Is.LessThanOrEqualTo(300));
            Assert.That(rendered, Does.Contain("moreText").And.Not.Contain("status="));
            Assert.That(backend.Operations.ScalarQueryCount, Is.EqualTo(queryCount));
            Assert.That(VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 300 }), Is.EqualTo(rendered));
        });
    }

    [Test]
    public void Build_WhenShortBodyFinishes_TransfersRemainingBudgetToLongBody()
    {
        var text = string.Concat(Enumerable.Repeat("remaining content ", 150));
        using var backend = CreateBackend(new Window(new TextBox("short") { Name = "first" }, new TextBox(text) { Name = "second" }));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 200 });
        var bodies = System.Text.RegularExpressions.Regex.Matches(rendered, @"<TextEdit\b[^>]*>([^<]*)</TextEdit>");
        Assert.Multiple(() =>
        {
            Assert.That(bodies, Has.Count.EqualTo(2));
            Assert.That(bodies[0].Groups[1].Value, Is.EqualTo("short"));
            Assert.That(bodies[1].Groups[1].Value.Length, Is.GreaterThan(100));
            Assert.That(Everywhere.Prompting.TokenHelper.EstimateTokenCount(rendered), Is.LessThanOrEqualTo(200));
        });
        var target = GetTargets(backend.Context).OfType<ElementTarget>().Single(target => ReferenceEquals(target.Element, backend.GetElement(1)));
        snapshot.Dispose();
        Assert.That(target.Element.ReadText().Text, Is.EqualTo(text));
    }

    [Test]
    public void Build_WhenCompositePreviewCutsSurrogatePair_ReportsMoreTextWithoutStatus()
    {
        using var backend = CreateBackend(new Window(new Text("A😀B"), new Text("tail")));
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(backend.Context, [backend.RootElement], allowedTraverseDirections: VisualContextTraverseDirections.Child);
        using var turn = backend.Context.BeginTurn();
        var rendered = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { MaximumCompositePreviewCharacters = 2 }).ToString();
        Assert.Multiple(() =>
        {
            Assert.That(rendered, Does.Contain("moreText>A</Composite>"));
            Assert.That(rendered, Does.Not.Contain("status="));
            Assert.That(snapshot.Status, Is.Empty);
        });
    }

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

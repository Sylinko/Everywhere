using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Everywhere.Chat;
using Everywhere.I18N;
using VisualContextLocaleKey = Everywhere.Automation.I18N.LocaleKey;

namespace Everywhere.Automation.Tests;

public sealed class ScenarioMockBackendTests
{
    [Test]
    public void QueryElement_WhenTextExceedsRequest_ReturnsBoundedSnapshot()
    {
        using var backend = CreateBackend(new Text("0123456789"));
        var request = new VisualElementQueryRequest(VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text, 5);
        var result = backend.RootElement.Query(request);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.Snapshot.Id, Is.EqualTo("test:42"));
            Assert.That(result.Snapshot.Type, Is.EqualTo(VisualElementType.Label));
            Assert.That(result.Snapshot.TextPreview, Is.EqualTo("01234"));
            Assert.That(result.Snapshot.HasMoreText, Is.True);
            Assert.That(backend.Operations.ScalarQueryCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateEnumerator_WhenCollectionIsHuge_RemainsLazyAndReportsMetadata()
    {
        var generatedItems = 0;
        var scenario = Scenario.Define("huge-list", context => new VirtualList(context, "items", 100_000, (_, index) =>
        {
            generatedItems++;
            return new Text($"item-{index}");
        }));
        using var backend = new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
        using var enumerator = backend.RootElement.CreateEnumerator(VisualElementRelation.Child, VisualElementQueryRequest.Default);

        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Count, Is.EqualTo(100_000));
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(enumerator.HasMore, Is.True);
            Assert.That(generatedItems, Is.Zero);
        });
        Assert.That(enumerator.MoveNext(), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Current.Snapshot.TextPreview, Is.EqualTo("item-0"));
            Assert.That(generatedItems, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateEnumerator_WhenCountIsUnavailable_PreservesLookaheadWithoutMaterialization()
    {
        using var backend = CreateBackend(new Panel(new Text("first"), new Text("second")), hasCount: false);
        using var enumerator = backend.RootElement.CreateEnumerator(VisualElementRelation.Child, VisualElementQueryRequest.Default);
        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Count, Is.EqualTo(-1));
            Assert.That(enumerator.HasMore, Is.True);
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MoveNext_WhenScenarioIsMutable_AdvancesExactlyOncePerAttempt()
    {
        using var backend = CreateBackend(new OnMoveNext(step => new Panel(new Text($"state-{step}-first"), new Text($"state-{step}-second"))));
        using var enumerator = backend.RootElement.CreateEnumerator(VisualElementRelation.Child, VisualElementQueryRequest.Default);
        Assert.That(enumerator.MoveNext(), Is.True);
        var first = enumerator.Current.Snapshot.TextPreview;
        Assert.That(enumerator.MoveNext(), Is.True);
        var second = enumerator.Current.Snapshot.TextPreview;
        Assert.That(enumerator.MoveNext(), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("state-1-first"));
            Assert.That(second, Is.EqualTo("state-2-second"));
            Assert.That(backend.Step, Is.EqualTo(3));
        });
    }

    [Test]
    public void QueryElement_WhenProviderTimesOut_ReturnsNormalizedFailure()
    {
        var failure = new VisualElementQueryFailure(VisualElementQueryFailureKind.Timeout, new DynamicLocaleKey(VisualContextLocaleKey.VisualContext_QueryFailure_Timeout), new TimeoutException());
        using var backend = CreateBackend(new Text("content"), failureProvider: _ => failure);
        var result = backend.RootElement.Query(VisualElementQueryRequest.Default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.AvailableFields, Is.EqualTo(VisualElementFields.None));
            Assert.That(result.MissingFields, Is.EqualTo(VisualElementFields.All));
            Assert.That(result.Failure, Is.SameAs(failure));
        });
    }

    [Test]
    public void ReadText_WhenContentSpansPages_ReturnsNumericOffsetsWithoutSkippingText()
    {
        using var backend = CreateBackend(new Text("0123456789"));

        var first = backend.RootElement.ReadText(maxCharacters: 4);
        var second = backend.RootElement.ReadText(first.NextOffset.GetValueOrDefault(), 4);
        var third = backend.RootElement.ReadText(second.NextOffset.GetValueOrDefault(), 4);

        Assert.Multiple(() =>
        {
            Assert.That(first.Text, Is.EqualTo("0123"));
            Assert.That(first.NextOffset, Is.EqualTo(4));
            Assert.That(second.Text, Is.EqualTo("4567"));
            Assert.That(second.NextOffset, Is.EqualTo(8));
            Assert.That(third.Text, Is.EqualTo("89"));
            Assert.That(third.NextOffset, Is.Null);
        });
    }

    [Test]
    public void ReadText_WhenControlHasNoText_ReturnsUnsupportedFailure()
    {
        using var backend = CreateBackend(new Button("Run"));

        var result = backend.RootElement.ReadText();

        Assert.Multiple(() =>
        {
            Assert.That(result.Text, Is.Null);
            Assert.That(result.NextOffset, Is.Null);
            Assert.That(result.Failure?.Kind, Is.EqualTo(VisualElementQueryFailureKind.Unsupported));
        });
    }

    [Test]
    public void Build_WhenUsingAutomationElements_ConsumesScenarioWithinBudget()
    {
        var generated = new VisualScenarioGenerator().Generate(CommonScenarios.Chat, 42);
        using var backend = new ScenarioMockBackend(generated);
        using var turn = backend.Context.BeginTurn();
        var limits = new VisualContextSnapshotLimits { MaximumNodes = 128, MaximumChildrenPerNode = 64, MaximumPlatformOperations = 512 };
        using var snapshot = VisualContextSnapshotter.CreateSnapshot(
            backend.Context,
            [backend.RootElement],
            limits,
            VisualContextTraverseDirections.Child);
        var output = VisualContextPromptBuilder.Build(backend.Context, snapshot, new VisualContextPromptOptions { TargetTokenBudget = 512 }).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(output, Is.Not.Empty);
            Assert.That(turn.Count, Is.GreaterThan(0));
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.LessThanOrEqualTo(limits.MaximumPlatformOperations));
            Assert.That(backend.Operations.ElementCreatedCount, Is.LessThanOrEqualTo(limits.MaximumPlatformOperations + backend.RootElements.Count));
        });
    }

    private static ScenarioMockBackend CreateBackend(VisualControl root, bool hasCount = true, VisualElementFields supportedFields = VisualElementFields.All, Func<string, VisualElementQueryFailure?>? failureProvider = null)
    {
        var scenario = Scenario.Define("test", _ => root);
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42), hasCount, supportedFields, failureProvider);
    }
}

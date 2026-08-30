using Everywhere.Chat;
using Everywhere.Core.Tests.Chat.VisualContext.Testing;
using Everywhere.I18N;
using Everywhere.Automation;
using Everywhere.Automation.Testing;
using VisualContextLocaleKey = Everywhere.Automation.I18N.LocaleKey;

namespace Everywhere.Core.Tests.Chat.VisualContext;

public sealed class ScenarioMockBackendTests
{
    [Test]
    public async Task QueryElement_WhenTextExceedsRequest_ReturnsBoundedSnapshot()
    {
        var backend = CreateBackend(new Text("0123456789"));
        using var runtimeLease = backend.CreateLease();
        var request = new VisualElementQueryRequest(
            VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text,
            5);

        var result = await runtimeLease.QueryElementAsync(backend.RootElement, request);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.IsPartial, Is.False);
            Assert.That(result.Snapshot.Id, Is.EqualTo("test:42"));
            Assert.That(result.Snapshot.Type, Is.EqualTo(VisualElementType.Label));
            Assert.That(result.Snapshot.TextPreview, Is.EqualTo("01234"));
            Assert.That(result.Snapshot.Name, Is.Null);
            Assert.That(result.AvailableFields, Is.EqualTo(request.RequestedFields));
            Assert.That(backend.Operations.ScalarQueryCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task CreateEnumerator_WhenCollectionIsHuge_RemainsLazyAndReportsMetadata()
    {
        var generatedItems = 0;
        var scenario = Scenario.Define(
            "huge-list",
            context => new VirtualList(
                context,
                "items",
                100_000,
                (_, index) =>
                {
                    generatedItems++;
                    return new Text($"item-{index}");
                }));
        var backend = new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
        using var runtimeLease = backend.CreateLease();
        await using var enumerator = await runtimeLease.CreateEnumeratorAsync(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        var hasMore = await enumerator.HasMoreAsync();
        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Count, Is.EqualTo(100_000));
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(hasMore, Is.True);
            Assert.That(generatedItems, Is.Zero);
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(1));
        });

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        hasMore = await enumerator.HasMoreAsync();

        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Index, Is.Zero);
            Assert.That(enumerator.Current.Snapshot.TextPreview, Is.EqualTo("item-0"));
            Assert.That(hasMore, Is.True);
            Assert.That(generatedItems, Is.EqualTo(1));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task CreateEnumerator_WhenCountIsUnavailable_PreservesLookaheadWithoutMaterialization()
    {
        var backend = CreateBackend(new Panel(new Text("first"), new Text("second")), hasCount: false);
        using var runtimeLease = backend.CreateLease();
        await using var enumerator = await runtimeLease.CreateEnumeratorAsync(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        var hasMore = await enumerator.HasMoreAsync();
        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Count, Is.EqualTo(-1));
            Assert.That(hasMore, Is.True);
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task MoveNext_WhenScenarioIsMutable_AdvancesExactlyOncePerAttempt()
    {
        var mutableRoot = new OnMoveNext(step => new Panel(
            new Text($"state-{step}-first"),
            new Text($"state-{step}-second")));
        var backend = CreateBackend(mutableRoot);
        using var runtimeLease = backend.CreateLease();
        await using var enumerator = await runtimeLease.CreateEnumeratorAsync(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        var first = enumerator.Current.Snapshot.TextPreview;
        Assert.That(await enumerator.MoveNextAsync(), Is.True);
        var second = enumerator.Current.Snapshot.TextPreview;
        Assert.That(await enumerator.MoveNextAsync(), Is.False);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo("state-1-first"));
            Assert.That(second, Is.EqualTo("state-2-second"));
            Assert.That(backend.Step, Is.EqualTo(3));
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.EqualTo(3));
        });
    }

    [Test]
    public async Task Dispose_WhenRuntimeLeaseEnds_EnumeratorBecomesUnavailableWithoutChangingOwnership()
    {
        var backend = CreateBackend(new Panel(new Text("content")));
        var runtimeLease = backend.CreateLease();
        var released = await runtimeLease.CreateEnumeratorAsync(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);
        var leaked = await runtimeLease.CreateEnumeratorAsync(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        await released.DisposeAsync();
        await released.DisposeAsync();
        runtimeLease.Dispose();
        runtimeLease.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(backend.Operations.EnumeratorCreatedCount, Is.EqualTo(2));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(1));
        });
        Assert.That(async () => await leaked.MoveNextAsync(), Throws.TypeOf<ObjectDisposedException>());

        await leaked.DisposeAsync();
    }

    [Test]
    public async Task QueryElement_WhenProviderTimesOut_ReturnsNormalizedFailure()
    {
        var failure = new VisualElementQueryFailure(
            VisualElementQueryFailureKind.Timeout,
            new DynamicLocaleKey(VisualContextLocaleKey.VisualContext_QueryFailure_Timeout),
            new TimeoutException());
        var backend = CreateBackend(new Text("content"), failureProvider: _ => failure);
        using var runtimeLease = backend.CreateLease();

        var result = await runtimeLease.QueryElementAsync(backend.RootElement, VisualElementQueryRequest.Default);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.False);
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.AvailableFields, Is.EqualTo(VisualElementFields.None));
            Assert.That(result.MissingFields, Is.EqualTo(VisualElementFields.All));
            Assert.That(result.Failure, Is.SameAs(failure));
        });
    }

    [Test]
    public async Task QueryElement_WhenFieldIsUnsupported_ReturnsAvailableSkeletonAndMissingFlags()
    {
        var backend = CreateBackend(
            new Text("content"),
            supportedFields: VisualElementFields.Id | VisualElementFields.Type);
        using var runtimeLease = backend.CreateLease();
        var request = new VisualElementQueryRequest(
            VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text,
            100);

        var result = await runtimeLease.QueryElementAsync(backend.RootElement, request);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.IsPartial, Is.True);
            Assert.That(result.Snapshot.Id, Is.EqualTo("test:42"));
            Assert.That(result.Snapshot.Type, Is.EqualTo(VisualElementType.Label));
            Assert.That(result.Snapshot.TextPreview, Is.Null);
            Assert.That(
                result.AvailableFields,
                Is.EqualTo(VisualElementFields.Id | VisualElementFields.Type));
            Assert.That(result.MissingFields, Is.EqualTo(VisualElementFields.Text));
        });
    }

    [Test]
    public void Build_WhenUsingLegacyAdapter_ConsumesScenarioWithinBudget()
    {
        var generated = new VisualScenarioGenerator().Generate(CommonScenarios.Chat, 42);
        var backend = new ScenarioMockBackend(generated);
        var builder = new VisualContextBuilder(
            [backend.RootElement],
            512,
            0,
            VisualContextDetailLevel.Compact,
            VisualContextTraverseDirections.Child);

        var output = builder.Build(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(output, Is.Not.Empty);
            Assert.That(builder.BuiltVisualElements, Is.Not.Empty);
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.LessThan(200));
            Assert.That(backend.Operations.ElementCreatedCount, Is.LessThan(100));
        });
    }

    private static ScenarioMockBackend CreateBackend(
        VisualControl root,
        bool hasCount = true,
        VisualElementFields supportedFields = VisualElementFields.All,
        Func<string, VisualElementQueryFailure?>? failureProvider = null)
    {
        var scenario = Scenario.Define("test", _ => root);
        return new ScenarioMockBackend(
            new VisualScenarioGenerator().Generate(scenario, 42),
            hasCount,
            supportedFields,
            failureProvider);
    }
}

using Everywhere.Chat;
using Everywhere.Core.Tests.Chat.VisualContext.Testing;
using Everywhere.Interop;
using Everywhere.VisualContext.Testing;

namespace Everywhere.Core.Tests.Chat.VisualContext;

public sealed class ScenarioMockBackendTests
{
    [Test]
    public void ReadElement_WhenTextExceedsRequest_ReturnsBoundedSnapshot()
    {
        var backend = CreateBackend(new Text("0123456789"));
        using var session = backend.CreateSession();
        var request = new VisualElementReadRequest(
            VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text,
            5);

        var result = session.ReadElement(backend.RootElement, request);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsSuccess, Is.True);
            Assert.That(result.IsPartial, Is.False);
            Assert.That(result.Snapshot.Id, Is.EqualTo("test:42"));
            Assert.That(result.Snapshot.Type, Is.EqualTo(VisualElementType.Label));
            Assert.That(result.Snapshot.TextPreview, Is.EqualTo("01234"));
            Assert.That(result.Snapshot.Name, Is.Null);
            Assert.That(result.AvailableFields, Is.EqualTo(request.RequestedFields));
            Assert.That(backend.Operations.ScalarReadCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CreateEnumerator_WhenCollectionIsHuge_RemainsLazyAndReportsMetadata()
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
        using var session = backend.CreateSession();
        using var enumerator = session.CreateEnumerator(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        Assert.Multiple(() =>
        {
            Assert.That(enumerator.HasCount, Is.True);
            Assert.That(enumerator.Count, Is.EqualTo(100_000));
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(enumerator.HasMore, Is.True);
            Assert.That(generatedItems, Is.Zero);
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(1));
        });

        Assert.That(enumerator.MoveNext(), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(enumerator.Index, Is.Zero);
            Assert.That(enumerator.Current.Snapshot.TextPreview, Is.EqualTo("item-0"));
            Assert.That(enumerator.HasMore, Is.True);
            Assert.That(generatedItems, Is.EqualTo(1));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(2));
        });
    }

    [Test]
    public void CreateEnumerator_WhenCountIsUnavailable_PreservesLookaheadWithoutMaterialization()
    {
        var backend = CreateBackend(new Panel(new Text("first"), new Text("second")), exposesCounts: false);
        using var session = backend.CreateSession();
        using var enumerator = session.CreateEnumerator(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        Assert.Multiple(() =>
        {
            Assert.That(enumerator.HasCount, Is.False);
            Assert.That(() => _ = enumerator.Count, Throws.TypeOf<NotSupportedException>());
            Assert.That(enumerator.HasMore, Is.True);
            Assert.That(enumerator.Index, Is.EqualTo(-1));
            Assert.That(backend.Operations.ElementCreatedCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void MoveNext_WhenScenarioIsMutable_AdvancesExactlyOncePerAttempt()
    {
        var mutableRoot = new OnMoveNext(step => new Panel(
            new Text($"state-{step}-first"),
            new Text($"state-{step}-second")));
        var backend = CreateBackend(mutableRoot);
        using var session = backend.CreateSession();
        using var enumerator = session.CreateEnumerator(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

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
            Assert.That(backend.Operations.MoveNextAttemptCount, Is.EqualTo(3));
        });
    }

    [Test]
    public void Dispose_WhenEnumeratorIsLeaked_SessionDisposesItExactlyOnce()
    {
        var backend = CreateBackend(new Panel(new Text("content")));
        var session = backend.CreateSession();
        var released = session.CreateEnumerator(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);
        var leaked = session.CreateEnumerator(
            backend.RootElement,
            VisualElementRelation.Child,
            VisualElementEnumerationOptions.Default);

        released.Dispose();
        released.Dispose();
        session.Dispose();
        session.Dispose();

        Assert.Multiple(() =>
        {
            Assert.That(backend.Operations.EnumeratorCreatedCount, Is.EqualTo(2));
            Assert.That(backend.Operations.EnumeratorDisposedCount, Is.EqualTo(2));
            Assert.That(() => leaked.MoveNext(), Throws.TypeOf<ObjectDisposedException>());
        });
    }

    [Test]
    public void ReadElement_WhenProviderTimesOut_ReturnsNormalizedFailure()
    {
        var failure = new VisualElementReadFailure(
            VisualElementReadFailureKind.Timeout,
            "Mock provider timed out.",
            new TimeoutException());
        var backend = CreateBackend(new Text("content"), failureProvider: _ => failure);
        using var session = backend.CreateSession();

        var result = session.ReadElement(backend.RootElement, VisualElementReadRequest.Default);

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
    public void ReadElement_WhenFieldIsUnsupported_ReturnsAvailableSkeletonAndMissingFlags()
    {
        var backend = CreateBackend(
            new Text("content"),
            supportedFields: VisualElementFields.Id | VisualElementFields.Type);
        using var session = backend.CreateSession();
        var request = new VisualElementReadRequest(
            VisualElementFields.Id | VisualElementFields.Type | VisualElementFields.Text,
            100);

        var result = session.ReadElement(backend.RootElement, request);

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
        bool exposesCounts = true,
        VisualElementFields supportedFields = VisualElementFields.All,
        Func<string, VisualElementReadFailure?>? failureProvider = null)
    {
        var scenario = Scenario.Define("test", _ => root);
        return new ScenarioMockBackend(
            new VisualScenarioGenerator().Generate(scenario, 42),
            exposesCounts,
            supportedFields,
            failureProvider);
    }
}

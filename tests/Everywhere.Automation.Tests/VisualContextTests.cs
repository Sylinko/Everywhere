using Everywhere.Automation.Testing;
using Everywhere.Automation.Tests.Testing;
using Avalonia;

namespace Everywhere.Automation.Tests;

public sealed class VisualContextTests
{
    [Test]
    public void Commit_WhenTargetIsPublishedAcrossTurns_ReusesIdAndPromotesHistoricalLookup()
    {
        using var backend = CreateBackend();
        using (var firstTurn = backend.Context.BeginTurn())
        {
            var batch = backend.Context.BeginPublication();
            Assert.That(batch.Add(CreateTarget(backend.RootElement, "initial")), Is.EqualTo(1));
            batch.Commit();
            firstTurn.Complete();
        }

        using (var secondTurn = backend.Context.BeginTurn())
        {
            var batch = backend.Context.BeginPublication();
            var childId = batch.Add(CreateTarget(backend.GetElement(0), "child"));
            batch.Commit();

            Assert.That(backend.Context.TryGetTarget(1, out var promotedTarget), Is.True);
            var updateBatch = backend.Context.BeginPublication();
            var repeatedId = updateBatch.Add(CreateTarget(backend.RootElement, "updated"));
            updateBatch.Commit();
            Assert.Multiple(() =>
            {
                Assert.That(repeatedId, Is.EqualTo(1));
                Assert.That(childId, Is.EqualTo(2));
                Assert.That(promotedTarget?.Status, Is.EqualTo(new[] { "initial" }));
                Assert.That(backend.Context.TryGetTarget(1, out var target), Is.True);
                Assert.That(target?.Status, Is.EqualTo(new[] { "updated" }));
            });
            secondTurn.Complete();
        }

        backend.Context.TrimRetainedTurns(1);
        Assert.Multiple(() =>
        {
            Assert.That(backend.Context.TargetCount, Is.EqualTo(2));
            Assert.That(backend.Context.RetainedTurnCount, Is.EqualTo(1));
            Assert.That(backend.Context.NextTargetId, Is.EqualTo(3));
        });
    }

    [Test]
    public void Abandon_WhenTargetWasProvisionallyAssigned_DoesNotConsumeId()
    {
        using var backend = CreateBackend();
        using var turn = backend.Context.BeginTurn();
        var abandonedBatch = backend.Context.BeginPublication();
        Assert.That(abandonedBatch.Add(CreateTarget(backend.RootElement, "abandoned")), Is.EqualTo(1));

        var committedBatch = backend.Context.BeginPublication();
        var committedId = committedBatch.Add(CreateTarget(backend.RootElement, "committed"));
        committedBatch.Commit();

        Assert.Multiple(() =>
        {
            Assert.That(committedId, Is.EqualTo(1));
            Assert.That(backend.Context.TargetCount, Is.EqualTo(1));
            Assert.That(backend.Context.NextTargetId, Is.EqualTo(2));
        });
    }

    [Test]
    public void Retentions_WhenCanonicalElementHasOverlappingOwners_ReleasesAfterLastOwner()
    {
        using var backend = CreateBackend();
        var identityMap = backend.Context.GetIdentityMap<TestIdentity>();
        using var firstOwner = backend.Context.CreateRetention();
        var first = identityMap.GetOrAdd(firstOwner, new TestIdentity(42), backend.Context, static (_, context) => new TestVisualElement(context, "test:42"));
        var secondOwner = backend.Context.CreateRetention();
        var second = identityMap.GetOrAdd(secondOwner, new TestIdentity(42), backend.Context, static (_, context) => new TestVisualElement(context, "test:42"));

        Assert.That(second, Is.SameAs(first));
        firstOwner.Dispose();
        Assert.That(first.ReleaseCount, Is.Zero);
        secondOwner.Dispose();
        Assert.That(first.ReleaseCount, Is.EqualTo(1));

        using var replacementOwner = backend.Context.CreateRetention();
        var replacement = identityMap.GetOrAdd(replacementOwner, new TestIdentity(42), backend.Context, static (_, context) => new TestVisualElement(context, "test:42"));
        Assert.That(replacement, Is.Not.SameAs(first));
    }

    [Test]
    public void CompleteTurn_WhenCompositeIsPublished_RetainsEveryDistinctSourceMemberUntilTurnEviction()
    {
        using var context = new VisualContext();
        using var acquisition = context.CreateRetention();
        var identityMap = context.GetIdentityMap<TestIdentity>();
        var first = identityMap.GetOrAdd(acquisition, new TestIdentity(1), context, static (_, owner) => new TestVisualElement(owner, "test:1"));
        var second = identityMap.GetOrAdd(acquisition, new TestIdentity(2), context, static (_, owner) => new TestVisualElement(owner, "test:2"));
        using (var turn = context.BeginTurn())
        {
            var batch = context.BeginPublication();
            batch.Add(new CompositeTarget
            {
                Parts =
                [
                    new CompositePart { Element = first, Snapshot = default },
                    new CompositePart { Element = second, Snapshot = default },
                    new CompositePart { Element = first, Snapshot = default },
                ],
            });
            batch.Commit();
            turn.Complete();
        }

        acquisition.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(first.ReleaseCount, Is.Zero);
            Assert.That(second.ReleaseCount, Is.Zero);
        });

        context.TrimRetainedTurns(0);
        Assert.Multiple(() =>
        {
            Assert.That(first.ReleaseCount, Is.EqualTo(1));
            Assert.That(second.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void TrimRetainedTurns_WhenTurnsShareElements_ReleasesOnlyAfterLastOwningTurn()
    {
        using var backend = CreateBackend();
        var identityMap = backend.Context.GetIdentityMap<TestIdentity>();
        using var acquisition = backend.Context.CreateRetention();
        var firstOnly = identityMap.GetOrAdd(acquisition, new TestIdentity(1), backend.Context, static (_, context) => new TestVisualElement(context, "test:1"));
        var shared = identityMap.GetOrAdd(acquisition, new TestIdentity(2), backend.Context, static (_, context) => new TestVisualElement(context, "test:2"));
        var secondOnly = identityMap.GetOrAdd(acquisition, new TestIdentity(3), backend.Context, static (_, context) => new TestVisualElement(context, "test:3"));

        using (var firstTurn = backend.Context.BeginTurn())
        {
            var batch = backend.Context.BeginPublication();
            batch.Add(CreateTarget(firstOnly, "first"));
            batch.Add(CreateTarget(shared, "shared-first"));
            batch.Commit();
            firstTurn.Complete();
        }

        using (var secondTurn = backend.Context.BeginTurn())
        {
            var batch = backend.Context.BeginPublication();
            batch.Add(CreateTarget(shared, "shared-second"));
            batch.Add(CreateTarget(secondOnly, "second"));
            batch.Commit();
            secondTurn.Complete();
        }

        acquisition.Dispose();
        backend.Context.TrimRetainedTurns(1);
        Assert.Multiple(() =>
        {
            Assert.That(firstOnly.ReleaseCount, Is.EqualTo(1));
            Assert.That(shared.ReleaseCount, Is.Zero);
            Assert.That(secondOnly.ReleaseCount, Is.Zero);
        });

        backend.Context.TrimRetainedTurns(0);
        Assert.Multiple(() =>
        {
            Assert.That(shared.ReleaseCount, Is.EqualTo(1));
            Assert.That(secondOnly.ReleaseCount, Is.EqualTo(1));
        });
    }

    [Test]
    public void CompleteTurn_WhenRetentionPolicyIsExceeded_EvictsOldestWholeTurns()
    {
        using var context = new VisualContext(2, 100);
        using var acquisition = context.CreateRetention();
        var identityMap = context.GetIdentityMap<TestIdentity>();
        var elements = Enumerable.Range(1, 3).Select(value => identityMap.GetOrAdd(acquisition, new TestIdentity(value), (Context: context, Value: value), static (_, state) => new TestVisualElement(state.Context, $"test:{state.Value}"))).ToArray();

        foreach (var element in elements)
        {
            using var turn = context.BeginTurn();
            var batch = context.BeginPublication();
            batch.Add(CreateTarget(element, element.Id));
            batch.Commit();
            turn.Complete();
        }

        acquisition.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(context.RetainedTurnCount, Is.EqualTo(2));
            Assert.That(context.TargetCount, Is.EqualTo(2));
            Assert.That(elements[0].ReleaseCount, Is.EqualTo(1));
            Assert.That(elements[1].ReleaseCount, Is.Zero);
            Assert.That(elements[2].ReleaseCount, Is.Zero);
        });
    }

    [Test]
    public void CompleteTurn_WhenTargetSoftLimitIsExceeded_PreservesNewestTurn()
    {
        using var context = new VisualContext(8, 2);
        using var acquisition = context.CreateRetention();
        var identityMap = context.GetIdentityMap<TestIdentity>();
        var elements = Enumerable.Range(1, 4).Select(value => identityMap.GetOrAdd(acquisition, new TestIdentity(value), (Context: context, Value: value), static (_, state) => new TestVisualElement(state.Context, $"test:{state.Value}"))).ToArray();

        for (var turnIndex = 0; turnIndex < 3; turnIndex++)
        {
            using var turn = context.BeginTurn();
            var batch = context.BeginPublication();
            var startIndex = turnIndex;
            var endIndex = turnIndex == 2 ? 4 : turnIndex + 1;
            for (var elementIndex = startIndex; elementIndex < endIndex; elementIndex++) batch.Add(CreateTarget(elements[elementIndex], elements[elementIndex].Id));
            batch.Commit();
            turn.Complete();
        }

        acquisition.Dispose();
        Assert.Multiple(() =>
        {
            Assert.That(context.RetainedTurnCount, Is.EqualTo(1));
            Assert.That(context.TargetCount, Is.EqualTo(2));
            Assert.That(elements[0].ReleaseCount, Is.EqualTo(1));
            Assert.That(elements[1].ReleaseCount, Is.EqualTo(1));
            Assert.That(elements[2].ReleaseCount, Is.Zero);
            Assert.That(elements[3].ReleaseCount, Is.Zero);
        });
    }

    private static ScenarioMockBackend CreateBackend()
    {
        var scenario = Scenario.Define("context", _ => new Panel(new Text("child")));
        return new ScenarioMockBackend(new VisualScenarioGenerator().Generate(scenario, 42));
    }

    private static ElementTarget CreateTarget(VisualElement element, string status) => new()
    {
        Element = element,
        Status = [status],
    };

    private sealed record TestIdentity(int Value);

    private sealed class TestVisualElement(VisualContext context, string id) : VisualElement(context, id)
    {
        public int ReleaseCount { get; private set; }

        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => throw new NotSupportedException();

        protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementEnumerationOptions options) => throw new NotSupportedException();

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) => Task.FromException<IVisualElementCapture>(new NotSupportedException());

        protected override void ReleaseCore() => ReleaseCount++;
    }

}

using Everywhere.Automation;

namespace Everywhere.Core.Tests.Automation;

public sealed class VisualTextQueryTests
{
    [Test]
    public void ReadText_WhenElementIsPaged_PreservesSurrogatePairBoundaries()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new ElementTarget
        {
            Element = CreateElement(context, retention, "emoji", "A😀B"),
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var query = new VisualQuery(context);
        var firstPage = query.ReadText(targetId, limit: 2);
        var secondPage = query.ReadText(targetId, 1, 2);
        var finalPage = query.ReadText(targetId, 3, 2);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage, Is.EqualTo("<visual-text target=1 offset=0 next=1>A</visual-text>"));
            Assert.That(secondPage, Is.EqualTo("<visual-text target=1 offset=1 next=3>😀</visual-text>"));
            Assert.That(finalPage, Is.EqualTo("<visual-text target=1 offset=3>B</visual-text>"));
        });
    }

    [Test]
    public void ReadText_WhenCurrentReadTimesOut_ReportsFailureAndPreservesOffset()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new ElementTarget
        {
            Element = CreateElement(context, retention, "timeout", null, new VisualElementQueryFailure(VisualElementQueryFailureKind.Timeout, null)),
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var query = new VisualQuery(context);
        var result = query.ReadText(targetId, 5, 64);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain("offset=5 next=5"));
            Assert.That(result, Does.Contain("status=\"Text reading timed out.\""));
            Assert.That(result, Does.Not.Contain("old structural"));
        });
    }

    [Test]
    public void ReadText_WhenCompositeCrossesMemberBoundary_ContinuesThroughObservedFallback()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var first = CreateElement(context, retention, "first", "abc");
        var second = CreateElement(context, retention, "second", "def");
        var fallback = CreateElement(context, retention, "fallback", null);
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(first),
                CreatePart(second),
                new CompositePart
                {
                    Element = fallback,
                    Snapshot = new VisualElementSnapshot(null, VisualElementType.Label, null, null, "ghi", false, null, null, null),
                },
            ],
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var query = new VisualQuery(context);
        var firstPage = query.ReadText(targetId, limit: 8);
        var secondPage = query.ReadText(targetId, 8, 8);

        Assert.Multiple(() =>
        {
            Assert.That(firstPage, Is.EqualTo($"<visual-text target=1 offset=0 next=8>abc{Environment.NewLine}def</visual-text>"));
            Assert.That(secondPage, Is.EqualTo($"<visual-text target=1 offset=8>{Environment.NewLine}ghi</visual-text>"));
        });
    }

    private static TextVisualElement CreateElement(VisualContext context, VisualElementRetention retention, string id, string? text, VisualElementQueryFailure? failure = null) =>
        context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
            retention,
            id,
            (Context: context, Text: text, Failure: failure),
            static (identity, state) => new TextVisualElement(state.Context, identity, state.Text, state.Failure));

    private static CompositePart CreatePart(VisualElement element) => new()
    {
        Element = element,
        Snapshot = new VisualElementSnapshot(null, VisualElementType.Label, null, null, null, false, null, null, null),
    };

    private sealed class TextVisualElement(VisualContext context, string id, string? text, VisualElementQueryFailure? failure) : VisualElement(context, id)
    {
        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => throw new NotSupportedException();

        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters)
        {
            if (failure is not null) return VisualElementTextReadResult.FromFailure(failure);
            return text is null ? VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null)) : VisualElementTextReadResult.FromSuccess(text, offset, maxCharacters);
        }

        protected override IVisualElementEnumerator CreateEnumeratorCore(VisualElementRelation relation, VisualElementQueryRequest request) => throw new NotSupportedException();

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) => Task.FromException<IVisualElementCapture>(new NotSupportedException());

        protected override void ReleaseCore() { }
    }
}

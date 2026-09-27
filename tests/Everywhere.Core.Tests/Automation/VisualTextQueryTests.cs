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
            Assert.That(firstPage, Is.EqualTo("<visual-text target=1 offset=0 next=1 total=4>A</visual-text>"));
            Assert.That(secondPage, Is.EqualTo("<visual-text target=1 offset=1 next=3 total=4>😀</visual-text>"));
            Assert.That(finalPage, Is.EqualTo("<visual-text target=1 offset=3 total=4>B</visual-text>"));
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
            Assert.That(result, Does.Contain("offset=5").And.Not.Contain("next="));
            Assert.That(result, Does.Contain("status=\"Text reading timed out\""));
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
        var fallback = CreateElement(context, retention, "fallback", "   ", name: "ghi");
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(first),
                CreatePart(second),
                new CompositePart
                {
                    Element = fallback,
                    ContentSource = CompositePartContentSource.Name,
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
            var totalLength = 9 + Environment.NewLine.Length * 2;
            Assert.That(firstPage, Is.EqualTo($"<visual-text target=1 offset=0 next=8 total={totalLength}>abc{Environment.NewLine}def</visual-text>"));
            Assert.That(secondPage, Is.EqualTo($"<visual-text target=1 offset=8 total={totalLength}>{Environment.NewLine}ghi</visual-text>"));
        });
    }

    [Test]
    public void ReadText_WhenOffsetIsNegative_ResolvesFromCurrentEndWithoutSplittingSurrogatePair()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new ElementTarget
        {
            Element = CreateElement(context, retention, "emoji-tail", "A😀"),
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var result = new VisualQuery(context).ReadText(targetId, -1, 1);

        Assert.That(result, Is.EqualTo("<visual-text target=1 offset=1 total=3>😀</visual-text>"));
    }

    [Test]
    public void ReadText_WhenCompositeMemberCannotBeRead_StopsBeforeTheGap()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var first = CreateElement(context, retention, "first", "abc");
        var unavailable = CreateElement(
            context,
            retention,
            "unavailable",
            null,
            new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null));
        var later = CreateElement(context, retention, "later", "must not appear");
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(first),
                new CompositePart
                {
                    Element = unavailable,
                    ContentSource = CompositePartContentSource.Text,
                },
                CreatePart(later),
            ],
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var result = new VisualQuery(context).ReadText(targetId);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain(">abc</visual-text>").And.Contain("status=").And.Not.Contain("must not appear"));
            Assert.That(result, Does.Not.Contain("next="));
        });
    }

    [Test]
    public void ReadText_WhenCompositeOffsetIsNegative_ReturnsCurrentLogicalTail()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(CreateElement(context, retention, "first-tail", "abc")),
                CreatePart(CreateElement(context, retention, "second-tail", "def")),
            ],
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var result = new VisualQuery(context).ReadText(targetId, -3, 8);
        var expectedOffset = 3 + Environment.NewLine.Length;

        Assert.That(result, Is.EqualTo($"<visual-text target=1 offset={expectedOffset} total={expectedOffset + 3}>def</visual-text>"));
    }

    [Test]
    public void ReadText_WhenCompositeNegativeOffsetSplitsSurrogatePair_IncludesTheCompleteScalar()
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(CreateElement(context, retention, "prefix", "abc")),
                CreatePart(CreateElement(context, retention, "emoji-tail", "A😀")),
            ],
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var result = new VisualQuery(context).ReadText(targetId, -1, 1);
        var expectedOffset = 3 + Environment.NewLine.Length + 1;

        Assert.That(result, Is.EqualTo($"<visual-text target=1 offset={expectedOffset} total={expectedOffset + 2}>😀</visual-text>"));
    }

    [TestCase(1, "B", true, CompositePartContentSource.Text)]
    [TestCase(2, "BC", false, CompositePartContentSource.Text)]
    [TestCase(8, "BC", false, CompositePartContentSource.Text)]
    [TestCase(1, "B", true, CompositePartContentSource.Name)]
    [TestCase(2, "BC", false, CompositePartContentSource.Name)]
    [TestCase(8, "BC", false, CompositePartContentSource.Name)]
    public void ReadText_WhenCompositeMemberTailStartsInsideSurrogatePair_PreservesTrailingText(
        int limit,
        string expectedText,
        bool hasNext,
        CompositePartContentSource contentSource)
    {
        using var context = new VisualContext();
        using var retention = context.CreateRetention();
        var target = new CompositeTarget
        {
            Parts =
            [
                CreatePart(CreateElement(context, retention, "prefix", "abc")),
                new CompositePart
                {
                    Element = CreateElement(context, retention, "emoji-tail", "A😀BC", name: "A😀BC"),
                    ContentSource = contentSource,
                },
            ],
        };

        using var turn = context.BeginTurn();
        var publication = context.BeginPublication();
        var targetId = publication.Add(target);
        publication.Commit();
        var query = new VisualQuery(context);
        var result = query.ReadText(targetId, -2, limit);
        var expectedOffset = 3 + Environment.NewLine.Length + 3;
        var nextAttribute = hasNext ? $" next={expectedOffset + 1}" : string.Empty;

        Assert.That(result, Is.EqualTo(
            $"<visual-text target=1 offset={expectedOffset}{nextAttribute} total={expectedOffset + 2}>{expectedText}</visual-text>"));
        if (hasNext)
        {
            Assert.That(query.ReadText(targetId, expectedOffset + 1, limit), Is.EqualTo(
                $"<visual-text target=1 offset={expectedOffset + 1} total={expectedOffset + 2}>C</visual-text>"));
        }
    }

    private static TextVisualElement CreateElement(
        VisualContext context,
        VisualElementRetention retention,
        string id,
        string? text,
        VisualElementQueryFailure? failure = null,
        string? name = null) =>
        context.GetIdentityMap<string>(StringComparer.Ordinal).GetOrAdd(
            retention,
            id,
            (Text: text, Failure: failure, Name: name),
            static (identity, state) => new TextVisualElement(identity, identity.Value, state.Text, state.Failure, state.Name));

    private static CompositePart CreatePart(VisualElement element) => new()
    {
        Element = element,
        ContentSource = CompositePartContentSource.Text,
    };

    private sealed class TextVisualElement(
        VisualElementIdentity identity,
        string id,
        string? text,
        VisualElementQueryFailure? failure,
        string? name) : VisualElement(identity, id)
    {
        protected override VisualElementQueryResult QueryCore(VisualElementQueryRequest request) => new(
            this,
            new VisualElementSnapshot(null, null, null, name, null, false, null, null, null),
            name is null ? VisualElementFields.None : VisualElementFields.Name,
            name is null ? request.RequestedFields : request.RequestedFields & ~VisualElementFields.Name,
            null);

        protected override VisualElementTextReadResult ReadTextCore(int offset, int maxCharacters, int maximumProbeCharacters)
        {
            if (failure is not null) return VisualElementTextReadResult.FromFailure(failure);
            return text is null ? VisualElementTextReadResult.FromFailure(new VisualElementQueryFailure(VisualElementQueryFailureKind.Unsupported, null)) : VisualElementTextReadResult.FromSuccess(text, offset, maxCharacters);
        }

        protected override IVisualElementCursor CreateEnumeratorCore(
            VisualElementRelation relation,
            VisualElementQueryRequest request,
            int offset,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        protected override Task<IVisualElementCapture> CaptureCoreAsync(CancellationToken cancellationToken) => Task.FromException<IVisualElementCapture>(new NotSupportedException());

        protected override void ReleaseCore() { }
    }
}

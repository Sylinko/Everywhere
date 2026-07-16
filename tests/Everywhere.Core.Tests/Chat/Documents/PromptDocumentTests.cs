using Everywhere.AI;
using Everywhere.Chat.Documents;
using MessagePack;

namespace Everywhere.Core.Tests.Chat.Documents;

public sealed class PromptDocumentTests
{
    [Test]
    public void DeclarativeConstructionStylesProduceTheSameDocument()
    {
        PromptNode? optional = null;
        PromptNode[] spread = ["three"];

        PromptDocument collectionExpression =
        [
            "one",
            optional,
            new PromptElement("section", "two")
            {
                Attributes = { ["index"] = 2 }
            },
            .. spread
        ];

        var objectInitializer = new PromptDocument
        {
            "one",
            optional,
            new PromptElement("section", "two")
            {
                Attributes = { ["index"] = 2 }
            },
            "three"
        };

        PromptDocument fluent = new PromptDocument().Children(
            "one",
            optional,
            new PromptElement("section", "two").Attribute("index", 2),
            "three");

        Assert.Multiple(() =>
        {
            Assert.That(collectionExpression.ToString(), Is.EqualTo("one<section index=\"2\">two</section>three"));
            Assert.That(objectInitializer.ToString(), Is.EqualTo(collectionExpression.ToString()));
            Assert.That(fluent.ToString(), Is.EqualTo(collectionExpression.ToString()));
            Assert.That(fluent, Is.TypeOf<PromptDocument>());
        });
    }

    [Test]
    public void StandaloneNodeRendersItsOwnLocalLimit()
    {
        const string required = "required result ";
        var optional = string.Join(' ', Enumerable.Repeat("optional", 80));
        PromptNode node = new PromptTokenLimit(
            TokenHelper.EstimateTokenCount(required),
            new PromptText(required).WithPriority(10),
            new PromptText(optional).WithPriority(0));

        Assert.That(node.ToString(), Is.EqualTo(required));
    }

    [Test]
    public void SingleContainerArgumentsRemainNestedAcrossConstructionStyles()
    {
        var constructorChild = new PromptElement("child", "constructor");
        var chunkChild = new PromptElement("child", "chunk");
        var fluentChild = new PromptElement("child", "fluent");
        PromptDocument document =
        [
            new PromptElement("parent", constructorChild),
            new PromptChunk(chunkChild),
            new PromptGroup().Children(fluentChild)
        ];

        Assert.That(
            document.ToString(),
            Is.EqualTo("<parent><child>constructor</child></parent><child>chunk</child><child>fluent</child>"));
    }

    [Test]
    public void ElementsEscapeTextAndAttributesButTopLevelTextStaysRaw()
    {
        PromptDocument document =
        [
            "raw <instruction>",
            new PromptElement("match", "x < y & z").Attribute("path", "a&\"b")
        ];

        Assert.That(
            document.ToString(),
            Is.EqualTo("raw <instruction><match path=\"a&amp;&quot;b\">x &lt; y &amp; z</match>"));
        // ReSharper disable once ObjectCreationAsStatement
        Assert.Throws<ArgumentException>(() => new PromptElement("not valid"));
        Assert.Throws<ArgumentException>(() => new PromptElement("valid").Attribute("not valid", 1));
    }

    [Test]
    public void PriorityIsLocalToItsNearestContainer()
    {
        var nestedLow = LongText("nested-low").WithPriority(0);
        var nestedHigh = LongText("nested-high").WithPriority(100);
        var sibling = LongText("sibling").WithPriority(1);
        var group = new PromptGroup { Priority = 100 }.Children(nestedLow, nestedHigh);
        PromptDocument document = [group, sibling];
        var expected = nestedLow.Text + nestedHigh.Text;

        var result = document.Render(TokenHelper.EstimateTokenCount(expected));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(expected));
            Assert.That(result.OmittedNodes, Does.Contain(sibling));
            Assert.That(result.OmittedNodes, Does.Not.Contain(nestedLow));
        });
    }

    [Test]
    public void PassedPriorityMakesAContainerTransparent()
    {
        var nestedLow = LongText("nested-low").WithPriority(0);
        var nestedHigh = LongText("nested-high").WithPriority(100);
        var sibling = LongText("sibling").WithPriority(1);
        var group = new PromptGroup { Priority = 100 }
            .Children(nestedLow, nestedHigh)
            .WithPassedPriority();
        PromptDocument document = [group, sibling];
        var expected = nestedHigh.Text + sibling.Text;

        var result = document.Render(TokenHelper.EstimateTokenCount(expected));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(expected));
            Assert.That(result.OmittedNodes, Does.Contain(nestedLow));
            Assert.That(result.OmittedNodes, Does.Not.Contain(sibling));
        });
    }

    [Test]
    public void ElementPassesChildPriorityByDefault()
    {
        var elementText = LongText("element-low").WithPriority(0);
        var element = new PromptElement("context", elementText).WithPriority(100);
        var sibling = LongText("sibling").WithPriority(1);
        PromptDocument document = [element, sibling];

        var result = document.Render(TokenHelper.EstimateTokenCount(sibling.Text));

        Assert.Multiple(() =>
        {
            Assert.That(element.PassPriority, Is.True);
            Assert.That(result.Content, Is.EqualTo(sibling.Text));
            Assert.That(result.OmittedNodes, Does.Contain(elementText));
            Assert.That(result.OmittedNodes, Does.Contain(element));
        });
    }

    [Test]
    public void AtomicChunkIsRemovedAsAWhole()
    {
        var first = LongText("first").WithPriority(0);
        var second = LongText("second").WithPriority(100);
        var chunk = new PromptChunk(first, second).WithPriority(0);
        var sibling = LongText("sibling").WithPriority(1);
        PromptDocument document = [chunk, sibling];

        var result = document.Render(TokenHelper.EstimateTokenCount(sibling.Text));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(sibling.Text));
            Assert.That(result.OmittedNodes, Does.Contain(chunk));
            Assert.That(result.OmittedNodes, Does.Contain(first));
            Assert.That(result.OmittedNodes, Does.Contain(second));
        });
    }

    [Test]
    public void EqualPriorityUsesDeclarationOrderAsTheTieBreaker()
    {
        var first = LongText("first").WithPriority(0);
        var second = LongText("second").WithPriority(0);
        var third = LongText("third").WithPriority(0);
        PromptDocument document = [first, second, third];
        var expected = second.Text + third.Text;

        var result = document.Render(TokenHelper.EstimateTokenCount(expected));

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(expected));
            Assert.That(result.OmittedNodes, Does.Contain(first));
        });
    }

    [Test]
    public void TextChunkShortensAtCompleteLineBoundaries()
    {
        var chunk = new PromptTextChunk("first line\nsecond line\nthird line")
            .BreakOnLines()
            .WithMaxTokens(TokenHelper.EstimateTokenCount("first line\nsecond line\n"));
        PromptDocument document = [chunk];

        var result = document.Render(100);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("first line\nsecond line\n"));
            Assert.That(result.TruncatedNodes, Is.EquivalentTo(new[] { chunk }));
            Assert.That(result.IncludedNodes, Does.Contain(chunk));
        });
    }

    [Test]
    public void TokenLimitPrunesAComplexXmlSubtreeWithinItsOwnBudget()
    {
        var lowText = LongText("xml-low").WithPriority(0);
        var highText = LongText("xml-high").WithPriority(100);
        var lowEntry = new PromptElement("entry", lowText).Attribute("rank", "low");
        var highEntry = new PromptElement("entry", highText).Attribute("rank", "high");
        var toolResult = new PromptElement("tool_result", lowEntry, highEntry)
            .Attribute("source", "demo");
        var expectedXml = $"<tool_result source=\"demo\"><entry rank=\"high\">{highText.Text}</entry></tool_result>";
        var limit = new PromptTokenLimit(TokenHelper.EstimateTokenCount(expectedXml), toolResult);
        PromptDocument document = ["prefix:", limit];

        var result = document.Render(int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo("prefix:" + expectedXml));
            Assert.That(result.OmittedNodes, Does.Contain(lowText));
            Assert.That(result.OmittedNodes, Does.Contain(lowEntry));
            Assert.That(result.IncludedNodes, Does.Contain(highEntry));
            Assert.That(result.IncludedNodes, Does.Contain(limit));
        });
    }

    [Test]
    public void NestedTokenLimitsAreAppliedFromInnerToOuter()
    {
        var innerLow = LongText("inner-low").WithPriority(0);
        var innerHigh = LongText("inner-high").WithPriority(100);
        var inner = new PromptTokenLimit(
            TokenHelper.EstimateTokenCount(innerHigh.Text),
            innerLow,
            innerHigh).WithPriority(100);
        var outerLow = LongText("outer-low").WithPriority(0);
        var outerHigh = LongText("outer-high").WithPriority(50);
        var expected = innerHigh.Text + outerHigh.Text;
        var outer = new PromptTokenLimit(TokenHelper.EstimateTokenCount(expected))
        {
            outerLow,
            inner,
            outerHigh
        };
        PromptDocument document = [outer];

        var result = document.Render(int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(expected));
            Assert.That(result.OmittedNodes, Does.Contain(innerLow));
            Assert.That(result.OmittedNodes, Does.Contain(outerLow));
            Assert.That(result.IncludedNodes, Does.Contain(innerHigh));
            Assert.That(result.IncludedNodes, Does.Contain(outerHigh));
        });
    }

    [Test]
    public void TokenLimitAboveTheGlobalBudgetDoesNotPreemptGlobalPriorityPruning()
    {
        var outside = LongText("outside-low").WithPriority(0);
        var firstInside = LongText("inside-first").WithPriority(1);
        var secondInside = LongText("inside-second").WithPriority(2);
        var expected = firstInside.Text + secondInside.Text;
        var globalBudget = TokenHelper.EstimateTokenCount(expected);
        var limit = new PromptTokenLimit(globalBudget + 1_000, firstInside, secondInside)
            .WithPriority(100);
        PromptDocument document = [outside, limit];

        var result = document.Render(globalBudget);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.EqualTo(expected));
            Assert.That(result.OmittedNodes, Does.Contain(outside));
            Assert.That(result.IncludedNodes, Does.Contain(firstInside));
            Assert.That(result.IncludedNodes, Does.Contain(secondInside));
        });
    }

    [Test]
    public void ZeroTokenLimitOmitsItsEntireSubtree()
    {
        var child = LongText("zero-budget");
        var limit = new PromptTokenLimit(0) { child };
        PromptDocument document = [limit];

        var result = document.Render(int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(result.Content, Is.Empty);
            Assert.That(result.TokenCount, Is.Zero);
            Assert.That(result.OmittedNodes, Does.Contain(child));
            Assert.That(result.OmittedNodes, Does.Contain(limit));
        });
    }

    [Test]
    public void TokenLimitRoundTripsThroughMessagePack()
    {
        PromptNode source = new PromptTokenLimit(42)
        {
            new PromptElement("result", "value").Attribute("kind", "tool")
        }.WithPriority(7);

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<PromptNode>(bytes);
        var restoredLimit = (PromptTokenLimit)restored;
        PromptDocument restoredDocument = [restoredLimit];

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.TypeOf<PromptTokenLimit>());
            Assert.That(restoredLimit.MaxTokens, Is.EqualTo(42));
            Assert.That(restoredLimit.Priority, Is.EqualTo(7));
            Assert.That(restoredLimit, Has.Count.EqualTo(1));
            Assert.That(restoredDocument.ToString(), Is.EqualTo("<result kind=\"tool\">value</result>"));
        });
    }

    [Test]
    public void TokenLimitedDocumentCanBeRenderedRepeatedlyWithoutMutation()
    {
        var optional = LongText("limited-optional").WithPriority(0);
        var required = LongText("limited-required").WithPriority(1);
        var limit = new PromptGroup().Children(optional, required)
            .LimitTokens(TokenHelper.EstimateTokenCount(required.Text));
        PromptDocument document = [limit];
        var original = document.ToString();

        var first = document.Render(int.MaxValue);
        var second = document.Render(int.MaxValue);

        Assert.Multiple(() =>
        {
            Assert.That(limit, Is.TypeOf<PromptTokenLimit>());
            Assert.That(first.Content, Is.EqualTo(required.Text));
            Assert.That(second.Content, Is.EqualTo(first.Content));
            Assert.That(second.TokenCount, Is.EqualTo(first.TokenCount));
            Assert.That(second.IncludedNodes, Is.EqualTo(first.IncludedNodes));
            Assert.That(second.OmittedNodes, Is.EqualTo(first.OmittedNodes));
            Assert.That(second.TruncatedNodes, Is.EqualTo(first.TruncatedNodes));
            Assert.That(document.ToString(), Is.EqualTo(original));
            Assert.That(limit, Has.Count.EqualTo(1));
            Assert.That(((PromptGroup)limit[0]).Children, Is.EqualTo(new[] { optional, required }));
        });
    }

    [Test]
    public void RenderingIsRepeatableAndDoesNotMutateTheDeclarationTree()
    {
        var optional = LongText("optional").WithPriority(0);
        var required = LongText("required").WithPriority(1);
        PromptDocument document = [optional, required];
        var original = document.ToString();
        var budget = TokenHelper.EstimateTokenCount(required.Text);

        var first = document.Render(budget);
        var second = document.Render(budget);

        Assert.Multiple(() =>
        {
            Assert.That(first.Content, Is.EqualTo(required.Text));
            Assert.That(second.Content, Is.EqualTo(first.Content));
            Assert.That(document.ToString(), Is.EqualTo(original));
            Assert.That(document, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void PolymorphicDocumentRoundTripsThroughMessagePack()
    {
        PromptNode source = new PromptDocument
        {
            new PromptText("plain").WithPriority(4),
            new PromptElement("section", new PromptTextChunk("one two three").BreakOnWhitespace())
                .Attribute("kind", "sample")
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<PromptNode>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored, Is.TypeOf<PromptDocument>());
            Assert.That(restored.Priority, Is.EqualTo(int.MaxValue));
            Assert.That(((PromptDocument)restored).ToString(), Is.EqualTo("plain<section kind=\"sample\">one two three</section>"));
            Assert.That(((PromptDocument)restored)[0].Priority, Is.EqualTo(4));
            Assert.That(((PromptElement)((PromptDocument)restored)[1]).PassPriority, Is.True);
        });
    }

    private static PromptText LongText(string marker) =>
        new($"[{marker}] " + string.Join(' ', Enumerable.Repeat(marker, 80)) + " ");
}

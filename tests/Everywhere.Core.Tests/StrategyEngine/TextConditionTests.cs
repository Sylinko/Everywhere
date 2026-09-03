using Everywhere.Chat;
using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.Conditions;

// Disambiguate from Sentry.AttachmentType, which the test project pulls in via a global using.
using AttachmentType = Everywhere.StrategyEngine.AttachmentType;

namespace Everywhere.Core.Tests.StrategyEngine;

public class TextConditionTests
{
    /// <summary>
    /// Regression test. <see cref="TextCondition.TextContains"/> is an optional filter, but the
    /// contains check used to be the unconditional return value, so leaving it unset made every
    /// evaluation fail and silently disabled all built-in text-selection strategies.
    /// </summary>
    [Test]
    public void Evaluate_WhenTextContainsIsUnset_MatchesAnyText()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            MinLength = 1,
            MinCount = 1
        };

        Assert.That(condition.Evaluate(ContextFor("hello world")), Is.True);
    }

    [Test]
    public void Evaluate_WhenTextContainsIsSet_FiltersByContent()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            TextContains = ["needle"]
        };

        Assert.Multiple(() =>
        {
            Assert.That(condition.Evaluate(ContextFor("a needle in a haystack")), Is.True);
            Assert.That(condition.Evaluate(ContextFor("just a haystack")), Is.False);
        });
    }

    [Test]
    public void Evaluate_WhenTextContainsIsSet_IgnoresCase()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            TextContains = ["NeEdLe"]
        };

        Assert.That(condition.Evaluate(ContextFor("a needle in a haystack")), Is.True);
    }

    [Test]
    public void Evaluate_WhenTextIsShorterThanMinLength_DoesNotMatch()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            MinLength = 50
        };

        Assert.Multiple(() =>
        {
            Assert.That(condition.Evaluate(ContextFor(new string('x', 49))), Is.False);
            Assert.That(condition.Evaluate(ContextFor(new string('x', 50))), Is.True);
        });
    }

    [Test]
    public void Evaluate_WhenTextIsLongerThanMaxLength_DoesNotMatch()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection,
            MaxLength = 10
        };

        Assert.Multiple(() =>
        {
            Assert.That(condition.Evaluate(ContextFor(new string('x', 11))), Is.False);
            Assert.That(condition.Evaluate(ContextFor(new string('x', 10))), Is.True);
        });
    }

    [Test]
    public void Evaluate_WhenAttachmentTypeDoesNotMatchTargetType_DoesNotMatch()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.Text
        };

        // The attachment is a TextSelectionAttachment, so a Text-only condition must ignore it.
        Assert.That(condition.Evaluate(ContextFor("hello world")), Is.False);
    }

    [Test]
    public void Evaluate_WhenNoAttachments_DoesNotMatch()
    {
        var condition = new TextCondition
        {
            TargetType = AttachmentType.TextSelection
        };

        Assert.That(condition.Evaluate(StrategyContext.FromAttachments([])), Is.False);
    }

    private static StrategyContext ContextFor(string text) =>
        StrategyContext.FromAttachments([new TextSelectionAttachment(text, null)]);
}

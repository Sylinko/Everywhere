using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.I18N;
using MessagePack;
using PromptRecipeSnapshot = Everywhere.AI.Prompts.PromptRecipeSnapshot;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptFoundationTests
{
    [Test]
    public void DefaultSystemPrompt_IncludesSkillsPromptPlaceholder()
    {
        Assert.That(
            DefaultPrompts.DefaultSystemPrompt,
            Does.Contain("{" + SystemPromptPlaceholderSource.SkillsPromptName + "}"));
    }

    [Test]
    public void PromptDiagnostic_CarriesCodeSeverityMessageSpanAndAction()
    {
        var messageKey = new DirectLocaleKey("Unknown placeholder.");
        var span = new PromptTextSpan(4, 7);

        var diagnostic = new PromptDiagnostic(
            PromptDiagnosticCode.UnknownPlaceholder,
            PromptDiagnosticSeverity.Warning,
            messageKey,
            span,
            "insert-default-prompt");

        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Code, Is.EqualTo(PromptDiagnosticCode.UnknownPlaceholder));
            Assert.That(diagnostic.Severity, Is.EqualTo(PromptDiagnosticSeverity.Warning));
            Assert.That(diagnostic.MessageKey, Is.SameAs(messageKey));
            Assert.That(diagnostic.Span, Is.EqualTo(span));
            Assert.That(diagnostic.ActionId, Is.EqualTo("insert-default-prompt"));
        });
    }

    [Test]
    public void PromptRecipeSnapshot_RoundTripsWithMessagePack()
    {
        var snapshot = new PromptRecipeSnapshot
        {
            SchemaVersion = 1,
            PersonaId = "programming-assistant",
            PreferredUserName = "DearVa",
            ScenarioIds =
            [
                "programming-and-development",
                "code-review"
            ],
            ToneId = "concise-and-direct",
            DetailLevelId = "balanced",
            OrganizationId = "conclusion-first",
            AdditionalRequirements = "Prefer focused examples.",
            IsDetachedFromRecipe = true
        };

        var bytes = MessagePackSerializer.Serialize(snapshot);
        var roundTripped = MessagePackSerializer.Deserialize<PromptRecipeSnapshot>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.SchemaVersion, Is.EqualTo(1));
            Assert.That(roundTripped.ScenarioIds, Is.EqualTo(new[] { "programming-and-development", "code-review" }));
            Assert.That(roundTripped.IsDetachedFromRecipe, Is.True);
        });
    }
}

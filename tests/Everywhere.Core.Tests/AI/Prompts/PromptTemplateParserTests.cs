using Everywhere.AI;
using Everywhere.AI.Prompts;
using PromptTemplateParser = Everywhere.AI.Prompts.PromptTemplateParser;
using PromptTemplateRenderer = Everywhere.AI.Prompts.PromptTemplateRenderer;

namespace Everywhere.Core.Tests.AI.PromptManager;

public sealed class PromptTemplateParserTests
{
    [Test]
    public void ParsePlaceholders_ReturnsNamesRawTextAndSpans()
    {
        var placeholders = PromptTemplateParser.ParsePlaceholders("A {Date} and {DefaultSystemPrompt}.");

        Assert.Multiple(() =>
        {
            Assert.That(placeholders, Has.Count.EqualTo(2));
            Assert.That(placeholders[0].Name, Is.EqualTo("Date"));
            Assert.That(placeholders[0].RawText, Is.EqualTo("{Date}"));
            Assert.That(placeholders[0].Span, Is.EqualTo(new PromptTextSpan(2, 6)));
            Assert.That(placeholders[1].Name, Is.EqualTo("DefaultSystemPrompt"));
            Assert.That(placeholders[1].RawText, Is.EqualTo("{DefaultSystemPrompt}"));
            Assert.That(placeholders[1].Span, Is.EqualTo(new PromptTextSpan(13, 21)));
        });
    }

    [Test]
    public void ParsePlaceholders_IgnoresEscapedAndTripleBraces()
    {
        var placeholders = PromptTemplateParser.ParsePlaceholders("{{OS}} {{{Date}}} {Time}");

        Assert.Multiple(() =>
        {
            Assert.That(placeholders, Has.Count.EqualTo(1));
            Assert.That(placeholders[0].Name, Is.EqualTo("Time"));
            Assert.That(placeholders[0].RawText, Is.EqualTo("{Time}"));
        });
    }

    [Test]
    public void Analyze_EmptyTemplate_ReturnsOnlyEmptyTemplateError()
    {
        var analysis = PromptTemplateAnalyzer.Analyze("   ");

        Assert.Multiple(() =>
        {
            Assert.That(analysis.Placeholders, Is.Empty);
            Assert.That(analysis.Diagnostics, Has.Count.EqualTo(1));
            Assert.That(analysis.Diagnostics[0].Code, Is.EqualTo(PromptDiagnosticCode.EmptyTemplate));
            Assert.That(analysis.Diagnostics[0].Severity, Is.EqualTo(PromptDiagnosticSeverity.Error));
        });
    }

    [Test]
    public void Analyze_UnknownPlaceholder_CarriesPlaceholderSpan()
    {
        var analysis = PromptTemplateAnalyzer.Analyze("{Mystery}\n{DefaultSystemPrompt}");

        var diagnostic = analysis.Diagnostics.Single();
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Code, Is.EqualTo(PromptDiagnosticCode.UnknownPlaceholder));
            Assert.That(diagnostic.Severity, Is.EqualTo(PromptDiagnosticSeverity.Warning));
            Assert.That(diagnostic.Span, Is.EqualTo(new PromptTextSpan(0, 9)));
        });
    }

    [Test]
    public void Analyze_MissingDefaultPrompt_DoesNotReportMissingSkillsWhenSkillsPlaceholderExists()
    {
        var analysis = PromptTemplateAnalyzer.Analyze("{SkillsPrompt}\nCustom behavior");

        Assert.Multiple(() =>
        {
            Assert.That(
                analysis.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
            Assert.That(
                analysis.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingSkillsPrompt));
        });
    }

    [Test]
    public void Analyze_MissingSkillsPrompt_OnlyWhenDefaultPromptIsBypassed()
    {
        var bypassingDefault = PromptTemplateAnalyzer.Analyze("Custom behavior");
        var includingDefault = PromptTemplateAnalyzer.Analyze("{DefaultSystemPrompt}\nCustom behavior");

        Assert.Multiple(() =>
        {
            Assert.That(
                bypassingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
            Assert.That(
                bypassingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Contain(PromptDiagnosticCode.MissingSkillsPrompt));
            Assert.That(
                includingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
            Assert.That(
                includingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingSkillsPrompt));
        });
    }

    [Test]
    public void RenderWithDiagnostics_ReturnsRenderedTextPlaceholdersAndDiagnostics()
    {
        var result = PromptTemplateRenderer.RenderWithDiagnostics(
            "{DefaultSystemPrompt} {Unknown}",
            key => key == "DefaultSystemPrompt" ? "Base" : null);

        Assert.Multiple(() =>
        {
            Assert.That(result.RenderedText, Is.EqualTo("Base {Unknown}"));
            Assert.That(result.Placeholders, Has.Count.EqualTo(2));
            Assert.That(result.Diagnostics.Select(static diagnostic => diagnostic.Code), Does.Contain(PromptDiagnosticCode.UnknownPlaceholder));
        });
    }
}

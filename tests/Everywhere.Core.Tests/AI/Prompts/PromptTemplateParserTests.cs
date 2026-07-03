using Everywhere.AI.Prompts;
using Everywhere.Core.I18N;
using Everywhere.I18N;
using PromptTemplateParser = Everywhere.AI.Prompts.PromptTemplateParser;
using PromptTemplateRenderer = Everywhere.AI.Prompts.PromptTemplateRenderer;

namespace Everywhere.Core.Tests.AI.Prompts;

public sealed class PromptTemplateParserTests
{
    [Test]
    public void ParsePlaceholders_RecognizesSingleBracePlaceholders()
    {
        var placeholders = PromptTemplateParser.ParsePlaceholders("{{OS}} {Date} and {DefaultSystemPrompt}.");

        Assert.Multiple(() =>
        {
            Assert.That(placeholders, Has.Count.EqualTo(2));
            Assert.That(placeholders.Select(static placeholder => placeholder.Name), Is.EqualTo(new[] { "Date", "DefaultSystemPrompt" }));
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
            Assert.That(
                ((DynamicLocaleKey)analysis.Diagnostics[0].MessageKey).Key,
                Is.EqualTo(LocaleKey.PromptDiagnostic_EmptyTemplate));
        });
    }

    [Test]
    public void Analyze_UnknownPlaceholder_ReturnsWarning()
    {
        var analysis = PromptTemplateAnalyzer.Analyze("{Mystery}\n{DefaultSystemPrompt}");

        var diagnostic = analysis.Diagnostics.Single(static diagnostic => diagnostic.Code == PromptDiagnosticCode.UnknownPlaceholder);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostic.Code, Is.EqualTo(PromptDiagnosticCode.UnknownPlaceholder));
            Assert.That(diagnostic.Severity, Is.EqualTo(PromptDiagnosticSeverity.Warning));
            Assert.That(diagnostic.MessageKey, Is.TypeOf<FormattedDynamicLocaleKey>());
            Assert.That(
                ((DynamicLocaleKey)diagnostic.MessageKey).Key,
                Is.EqualTo(LocaleKey.PromptDiagnostic_UnknownPlaceholder));
        });
    }

    [Test]
    public void Analyze_DateTimePlaceholders_ReportFreshnessAndCacheHints()
    {
        var withoutDateOrTime = PromptTemplateAnalyzer.Analyze("Custom behavior");
        var timeWithoutDate = PromptTemplateAnalyzer.Analyze("{Time}\nCustom behavior");
        var withDate = PromptTemplateAnalyzer.Analyze("{Date}\nCustom behavior");

        Assert.Multiple(() =>
        {
            Assert.That(
                withoutDateOrTime.Diagnostics.Single(static diagnostic => diagnostic.Code == PromptDiagnosticCode.MissingDate).Severity,
                Is.EqualTo(PromptDiagnosticSeverity.Info));
            Assert.That(
                timeWithoutDate.Diagnostics.Single(static diagnostic => diagnostic.Code == PromptDiagnosticCode.TimeMayReduceCacheHitRate).Severity,
                Is.EqualTo(PromptDiagnosticSeverity.Warning));
            Assert.That(
                withDate.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingDate));
            Assert.That(
                withDate.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.TimeMayReduceCacheHitRate));
        });
    }

    [Test]
    public void Analyze_MissingDefaultSystemPrompt_DoesNotReportDiagnostic()
    {
        var analysis = PromptTemplateAnalyzer.Analyze("{SkillsPrompt}\nCustom behavior");

        Assert.Multiple(() =>
        {
            Assert.That(
                analysis.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
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
        var includingSkills = PromptTemplateAnalyzer.Analyze("{SkillsPrompt}\nCustom behavior");

        Assert.Multiple(() =>
        {
            Assert.That(
                bypassingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
            Assert.That(
                bypassingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Contain(PromptDiagnosticCode.MissingSkillsPrompt));
            Assert.That(
                includingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingDefaultSystemPrompt));
            Assert.That(
                includingDefault.Diagnostics.Select(static diagnostic => diagnostic.Code),
                Does.Not.Contain(PromptDiagnosticCode.MissingSkillsPrompt));
            Assert.That(
                includingSkills.Diagnostics.Select(static diagnostic => diagnostic.Code),
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

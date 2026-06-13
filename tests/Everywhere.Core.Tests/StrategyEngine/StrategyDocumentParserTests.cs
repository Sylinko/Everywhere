using Everywhere.I18N;
using Everywhere.StrategyEngine;

namespace Everywhere.Core.Tests.StrategyEngine;

public class StrategyDocumentParserTests
{
    [Test]
    public void Parse_MinimalValidStrategy()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\demo.strategy.md",
            """
            ---
            schema: everywhere.strategy/v1
            name: demo
            title: "Demo title"
            priority: 10
            preprocessors:
              - selected-text
            ---

            Body.
            """,
            "user");

        var definition = (StrategyDefinitionV1)document.Definition;
        Assert.Multiple(() =>
        {
            Assert.That(document.Diagnostics, Is.Empty);
            Assert.That(document.Schema, Is.EqualTo(StrategyDefinitionV1.DefaultSchema));
            Assert.That(document.HasBodySection, Is.True);
            Assert.That(definition.Name, Is.EqualTo("demo"));
            Assert.That(definition.TitleKey, Is.TypeOf<DirectResourceKey>());
            Assert.That(definition.TitleKey?.ToString(), Is.EqualTo("Demo title"));
            Assert.That(definition.Priority, Is.EqualTo(10));
            Assert.That(definition.Preprocessors, Is.EqualTo(new[] { "selected-text" }));
            Assert.That(definition.Body, Is.EqualTo("\nBody."));
        });
    }

    [Test]
    public void Parse_TitleMappingCreatesJsonDynamicResourceKey()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\demo.strategy.md",
            """
            ---
            name: demo
            title:
              en: Demo
              zh-hans: 演示
            ---
            Body.
            """,
            "user");

        var definition = (StrategyDefinitionV1)document.Definition;

        Assert.Multiple(() =>
        {
            Assert.That(document.Diagnostics, Is.Empty);
            Assert.That(definition.TitleKey, Is.TypeOf<JsonDynamicResourceKey>());
            var title = (JsonDynamicResourceKey)definition.TitleKey!;
            Assert.That(title["en"], Is.EqualTo("Demo"));
            Assert.That(title["zh-hans"], Is.EqualTo("演示"));
        });
    }

    [Test]
    public void Parse_InvalidTitleShapeReturnsDiagnostic()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\demo.strategy.md",
            """
            ---
            name: demo
            title:
              en:
                nested: bad
            ---
            Body.
            """,
            "user");

        Assert.That(document.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.invalid_title"));
    }

    [Test]
    public void Parse_InvalidYamlReturnsDiagnostic()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\broken.strategy.md",
            """
            ---
            name: [
            ---
            Body.
            """,
            "user");

        Assert.That(document.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.invalid_yaml"));
    }

    [Test]
    public void Parse_InvalidToolsShapeReturnsDiagnostic()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\tools.strategy.md",
            """
            ---
            name: tools
            tools:
              builtin.web.*: yes
            ---
            Body.
            """,
            "user");

        Assert.That(document.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.invalid_tools"));
    }

    [Test]
    public void Parse_InvalidDurationReturnsDiagnostic()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\duration.strategy.md",
            """
            ---
            name: duration
            options:
              matchingTimeout: 1m
            ---
            Body.
            """,
            "user");

        Assert.That(document.Diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("strategy.invalid_duration"));
    }

    [Test]
    public void Parse_BodyPreservedExceptNormalizedLineEndings()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\body.strategy.md",
            "---\r\nname: body\r\n---\r\n\r\nLine 1\r\nLine 2\r\n",
            "user");

        Assert.Multiple(() =>
        {
            Assert.That(document.Body, Is.EqualTo("\nLine 1\nLine 2\n"));
            Assert.That(((StrategyDefinitionV1)document.Definition).Body, Is.EqualTo("\nLine 1\nLine 2\n"));
        });
    }

    [Test]
    public void Parse_PreservesUnknownMetadataFields()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\metadata.strategy.md",
            """
            ---
            name: metadata
            author: Everywhere
            custom:
              nested: value
            ---
            Body.
            """,
            "user");

        var definition = (StrategyDefinitionV1)document.Definition;
        Assert.Multiple(() =>
        {
            Assert.That(definition.Metadata["author"], Is.EqualTo("Everywhere"));
            Assert.That(definition.Metadata["custom"], Is.TypeOf<Dictionary<string, object>>());
        });
    }
}

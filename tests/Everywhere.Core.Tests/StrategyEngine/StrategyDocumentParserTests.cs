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
            id: user.demo
            name: Demo
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
            Assert.That(definition.Id, Is.EqualTo("user.demo"));
            Assert.That(definition.Name, Is.EqualTo("Demo"));
            Assert.That(definition.Priority, Is.EqualTo(10));
            Assert.That(definition.Preprocessors, Is.EqualTo(new[] { "selected-text" }));
            Assert.That(definition.Body, Is.EqualTo("\nBody."));
        });
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
            id: user.tools
            name: Tools
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
            id: user.duration
            name: Duration
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
            "---\r\nid: user.body\r\nname: Body\r\n---\r\n\r\nLine 1\r\nLine 2\r\n",
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
            id: user.metadata
            name: Metadata
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

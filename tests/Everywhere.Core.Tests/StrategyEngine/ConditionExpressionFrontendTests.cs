using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.ConditionExpression;

namespace Everywhere.Core.Tests.StrategyEngine;

public class ConditionExpressionFrontendTests
{
    [Test]
    public void Compile_DottedAndNestedFormsProduceSameCanonicalTree()
    {
        var dotted = CompileWhen(
            """
            when:
              attachments.files.any.path.glob: "*.md"
            """);
        var nested = CompileWhen(
            """
            when:
              attachments:
                files:
                  any:
                    path:
                      glob: "*.md"
            """);

        Assert.That(dotted.Condition.Syntax.ToCanonicalString(), Is.EqualTo(nested.Condition.Syntax.ToCanonicalString()));
    }

    [Test]
    public void Compile_MergesCompatibleDottedAndNestedForms()
    {
        var result = CompileWhen(
            """
            when:
              attachments.files.any.path.glob: "*.md"
              attachments:
                files:
                  any:
                    path:
                      endsWith: ".txt"
            """);

        Assert.That(
            result.Condition.Syntax.ToCanonicalString(),
            Is.EqualTo(
                """
                attachments:
                  files:
                    any:
                      path:
                        endsWith: ".txt"
                        glob: "*.md"
                """));
    }

    [Test]
    public void Compile_DuplicateScalarCollisionProducesDiagnostic()
    {
        var (_, diagnostics) = TryCompileWhen(
            """
            when:
              environment.os.equals: "windows"
              environment:
                os:
                  equals: "linux"
            """);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("condition.dotted_collision"));
    }

    [Test]
    public void Compile_ScalarMappingCollisionProducesDiagnostic()
    {
        var (_, diagnostics) = TryCompileWhen(
            """
            when:
              environment.os: "windows"
              environment.os.equals: "linux"
            """);

        Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("condition.dotted_collision"));
    }

    [Test]
    public void Tokenize_ParsesIndexAndReverseIndex()
    {
        var diagnostics = new List<StrategyDiagnostic>();
        var tokenization = ConditionPathTokenizer.Tokenize(
            "attachments.files[0].related[^1].path",
            "when.attachments.files[0].related[^1].path",
            TestSource,
            diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(tokenization, Is.Not.Null);
            Assert.That(tokenization!.CanonicalSegments, Is.EqualTo(new[] { "attachments", "files[0]", "related[^1]", "path" }));
            Assert.That(tokenization.Tokens.Select(token => token.Kind), Contains.Item(ConditionPathTokenKind.Index));
            Assert.That(tokenization.Tokens.Select(token => token.Kind), Contains.Item(ConditionPathTokenKind.ReverseIndex));
        });
    }

    [Test]
    public void Tokenize_ParsesRanges()
    {
        var diagnostics = new List<StrategyDiagnostic>();
        var tokenization = ConditionPathTokenizer.Tokenize(
            "attachments.files[6..-1].path",
            "when.attachments.files[6..-1].path",
            TestSource,
            diagnostics);

        var range = tokenization!.Tokens.Single(token => token.Kind == ConditionPathTokenKind.Range);
        Assert.Multiple(() =>
        {
            Assert.That(diagnostics, Is.Empty);
            Assert.That(tokenization.CanonicalSegments, Is.EqualTo(new[] { "attachments", "files[6..-1]", "path" }));
            Assert.That(range.Start!.Value, Is.EqualTo(6));
            Assert.That(range.End!.Value, Is.EqualTo(-1));
        });
    }

    [Test]
    public void Tokenize_InvalidRangeProducesDiagnostic()
    {
        var diagnostics = new List<StrategyDiagnostic>();
        var tokenization = ConditionPathTokenizer.Tokenize(
            "attachments.files[1..2..3].path",
            "when.attachments.files[1..2..3].path",
            TestSource,
            diagnostics);

        Assert.Multiple(() =>
        {
            Assert.That(tokenization, Is.Null);
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Does.Contain("condition.invalid_yaml_shape"));
        });
    }

    [Test]
    public void Compile_LogicalSequenceIsPreserved()
    {
        var result = CompileWhen(
            """
            when:
              any:
                - attachments.selection.text:
                    length:
                      min: 1
                - attachments.files:
                    count:
                      min: 1
            """);

        Assert.That(result.Condition.Syntax.ToCanonicalString(), Does.Contain("any:"));
        Assert.That(result.Condition.Syntax.ToCanonicalString(), Does.Contain("-"));
    }

    [Test]
    public void Normalize_StructuredWhenCreatesPlaceholderCondition()
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            """
            ---
            name: dsl
            title: DSL
            when:
              attachments.selection.text:
                length:
                  min: 1
            ---
            Body.
            """,
            "user");
        var normalizer = new StrategyDefinitionV1Normalizer();

        var result = normalizer.NormalizeAsync(document, new StrategyLoadContext(), CancellationToken.None).GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics.Where(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
            Assert.That(result.Strategy!.Condition, Is.TypeOf<ConditionExpressionCondition>());
            Assert.That(result.Strategy.Condition!.Evaluate(StrategyContext.FromAttachments([])), Is.Null);
        });
    }

    private static (ConditionExpressionCondition Condition, IReadOnlyList<StrategyDiagnostic> Diagnostics) CompileWhen(string frontmatter)
    {
        var (condition, diagnostics) = TryCompileWhen(frontmatter);
        Assert.That(diagnostics.Where(diagnostic => diagnostic.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
        Assert.That(condition, Is.Not.Null);
        return (condition!, diagnostics);
    }

    private static (ConditionExpressionCondition? Condition, IReadOnlyList<StrategyDiagnostic> Diagnostics) TryCompileWhen(string frontmatter)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var condition = new StrategyConditionCompiler(StrategyOptions.Default)
            .Compile(definition.When!, TestSource, diagnostics) as ConditionExpressionCondition;
        return (condition, diagnostics);
    }

    private static StrategySource TestSource => new()
    {
        ProviderId = "user",
        Location = new Uri(@"C:\strategies\dsl.strategy.md")
    };
}

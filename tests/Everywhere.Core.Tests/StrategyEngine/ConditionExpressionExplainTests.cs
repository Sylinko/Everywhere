using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.ConditionExpression;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.Core.Tests.StrategyEngine;

public class ConditionExpressionExplainTests
{
    [Test]
    public void Explain_IncludesCanonicalSyntaxBoundPathsDiagnosticsAndShortCircuitShape()
    {
        var condition = Compile(
            """
            when:
              all:
                - tags:
                    containsAny: []
                - attachments.files:
                    any:
                      extension:
                        in: [".md"]
            """);

        var explain = condition.Explain;

        Assert.Multiple(() =>
        {
            Assert.That(explain.CanonicalSyntax, Does.Contain("attachments:"));
            Assert.That(explain.Paths.Select(path => path.PublicPath), Does.Contain("attachments.files"));
            Assert.That(explain.Paths.Select(path => path.PublicPath), Does.Contain("$item.extension"));
            Assert.That(explain.StaticDiagnostics.Select(d => d.Code), Does.Contain("condition.empty_contains_any"));
            Assert.That(explain.Text, Does.Contain("short-circuit: all(false), any(true), none(not-any)"));
            Assert.That(explain.Text, Does.Contain("null-behavior: false/null hide; true recommends"));
        });
    }

    private static ConditionExpressionCondition Compile(string frontmatter)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var binder = new ConditionExpressionBinder(
            DefaultRoots().Concat([new StrategyPathRootProvider<IReadOnlyList<string>>("tags", _ => new[] { "text" })]),
            new JsonStrategyPathAccessorProvider());
        var condition = new StrategyConditionCompiler(StrategyOptions.Default, binder)
            .Compile(definition.When!, TestSource, diagnostics) as ConditionExpressionCondition;

        Assert.That(diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
        Assert.That(condition, Is.Not.Null);
        return condition!;
    }

    private static StrategySource TestSource => new()
    {
        ProviderId = "user",
        Location = new Uri(@"C:\strategies\dsl.strategy.md")
    };

    private static IReadOnlyList<IStrategyPathRootProvider> DefaultRoots() =>
    [
        new StrategyPathRootProvider<StrategyAttachmentsRoot>("attachments", _ => null),
        new StrategyPathRootProvider<ProcessInfo?>("activeProcess", _ => null),
        new StrategyPathRootProvider<StrategyEnvironmentRoot>("environment", _ => StrategyEnvironmentRoot.Current),
        new StrategyPathRootProvider<ExtraContextSnapshot>("extra", _ => null, isDeferred: true)
    ];
}

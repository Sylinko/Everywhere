using Everywhere.Chat;
using Everywhere.I18N;
using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.ConditionExpression;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.Core.Tests.StrategyEngine;

public class ConditionExpressionEvaluationTests
{
    [Test]
    public void Evaluate_MissingPathReturnsNull()
    {
        var condition = Compile(
            """
            when:
              activeProcess.name:
                equals: code
            """);

        var result = condition.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.path_missing"));
        });
    }

    [Test]
    public void Evaluate_StaticAndRuntimeTypeMismatchAreDistinct()
    {
        var staticDiagnostics = CompileDiagnostics(
            """
            when:
              attachments.files:
                startsWith: a
            """);
        var condition = Compile(
            """
            when:
              amount:
                min: 1
            """,
            new StrategyPathRootProvider<int>("amount", _ => "not-number"));

        var result = condition.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(staticDiagnostics.Select(d => d.Code), Does.Contain("condition.type_mismatch"));
            Assert.That(result.Value, Is.False);
            Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.runtime_type_mismatch"));
        });
    }

    [Test]
    public void Evaluate_RegexTimeoutReturnsNull()
    {
        var condition = Compile(
            """
            when:
              attachments.texts[0].text:
                regex: "^(a+)+$"
            """,
            options: StrategyOptions.Default with { RegexTimeout = TimeSpan.FromTicks(1) });
        var context = StrategyContext.FromAttachments(
        [
            new TextAttachment(new DirectResourceKey("text"), new string('a', 20000) + "!")
        ]);

        var result = condition.EvaluateDetailed(context);

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("regex.timeout"));
        });
    }

    [Test]
    public void Evaluate_NoneOverDeferredExtraReturnsNull()
    {
        var condition = Compile(
            """
            when:
              extra.items:
                none:
                  value:
                    equals: a
            """);

        var result = condition.EvaluateDetailed(StrategyContext.FromAttachments([]));

        Assert.Multiple(() =>
        {
            Assert.That(result.Value, Is.Null);
            Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.deferred_root"));
            Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.root_unavailable"));
        });
    }

    [Test]
    public void Evaluate_ScalarCollectionMembershipWorks()
    {
        var contains = Compile(
            """
            when:
              tags:
                contains: image
            """,
            new StrategyPathRootProvider<IReadOnlyList<string>>("tags", _ => new[] { "text", "image" }));
        var containsAny = Compile(
            """
            when:
              tags:
                containsAny: [image, video]
            """,
            new StrategyPathRootProvider<IReadOnlyList<string>>("tags", _ => new[] { "text", "image" }));
        var containsAll = Compile(
            """
            when:
              tags:
                containsAll: [text, image]
            """,
            new StrategyPathRootProvider<IReadOnlyList<string>>("tags", _ => new[] { "text", "image" }));

        Assert.Multiple(() =>
        {
            Assert.That(contains.Evaluate(StrategyContext.FromAttachments([])), Is.True);
            Assert.That(containsAny.Evaluate(StrategyContext.FromAttachments([])), Is.True);
            Assert.That(containsAll.Evaluate(StrategyContext.FromAttachments([])), Is.True);
        });
    }

    [Test]
    public void Evaluate_ObjectCollectionPredicatesWork()
    {
        var any = Compile(
            """
            when:
              attachments.files:
                any:
                  extension:
                    in: [".md", ".txt"]
            """);
        var all = Compile(
            """
            when:
              attachments.files:
                all:
                  extension:
                    startsWith: "."
            """);
        var none = Compile(
            """
            when:
              attachments.files:
                none:
                  extension:
                    equals: ".png"
            """);
        var context = StrategyContext.FromAttachments(
        [
            File(@"C:\work\a.md", "text/markdown"),
            File(@"C:\work\b.txt", "text/plain")
        ]);

        Assert.Multiple(() =>
        {
            Assert.That(any.Evaluate(context), Is.True);
            Assert.That(all.Evaluate(context), Is.True);
            Assert.That(none.Evaluate(context), Is.True);
        });
    }

    [Test]
    public void Evaluate_ThreeValuedLogicalOperatorsShortCircuit()
    {
        var all = Compile(
            """
            when:
              all:
                - activeProcess.name:
                    equals: code
                - attachments.files:
                    count:
                      min: 1
            """);
        var any = Compile(
            """
            when:
              any:
                - activeProcess.name:
                    equals: code
                - attachments.files:
                    count:
                      min: 1
            """);
        var not = Compile(
            """
            when:
              not:
                activeProcess.name:
                  equals: code
            """);
        var context = StrategyContext.FromAttachments([File(@"C:\work\a.md", "text/markdown")]);

        Assert.Multiple(() =>
        {
            Assert.That(all.Evaluate(context), Is.Null);
            Assert.That(any.Evaluate(context), Is.True);
            Assert.That(not.Evaluate(context), Is.Null);
        });
    }

    [Test]
    public void Evaluate_StringComparisonIsCaseInsensitiveUnlessRequested()
    {
        var insensitive = Compile(
            """
            when:
              activeProcess.name:
                startsWith: CODE
            """);
        var sensitive = Compile(
            """
            when:
              activeProcess.name:
                caseSensitive: true
                startsWith: CODE
            """);
        var context = new StrategyContext
        {
            Attachments = [],
            ActiveProcess = new ProcessInfo(1, "code", null, null)
        };

        Assert.Multiple(() =>
        {
            Assert.That(insensitive.Evaluate(context), Is.True);
            Assert.That(sensitive.Evaluate(context), Is.False);
        });
    }

    [Test]
    public void Evaluate_IndexAndRangeRuntimeBehavior()
    {
        var index = Compile(
            """
            when:
              attachments.files[0].extension:
                equals: ".md"
            """);
        var outOfRange = Compile(
            """
            when:
              attachments.files[2].extension:
                equals: ".md"
            """);
        var range = Compile(
            """
            when:
              attachments.files[0..1]:
                count:
                  min: 1
                  max: 1
            """);
        var badRange = Compile(
            """
            when:
              attachments.files[3..4]:
                count:
                  min: 1
            """);
        var context = StrategyContext.FromAttachments([File(@"C:\work\a.md", "text/markdown")]);

        var missing = outOfRange.EvaluateDetailed(context);
        var empty = badRange.EvaluateDetailed(context);

        Assert.Multiple(() =>
        {
            Assert.That(index.Evaluate(context), Is.True);
            Assert.That(range.Evaluate(context), Is.True);
            Assert.That(missing.Value, Is.Null);
            Assert.That(missing.Diagnostics.Select(d => d.Code), Does.Contain("condition.index_out_of_range"));
            Assert.That(empty.Value, Is.False);
            Assert.That(empty.Diagnostics.Select(d => d.Code), Does.Contain("condition.range_out_of_bounds"));
        });
    }

    private static FileAttachment File(string path, string mimeType) =>
        new(new DirectResourceKey(Path.GetFileName(path)), path, "sha", mimeType);

    private static IReadOnlyList<StrategyDiagnostic> CompileDiagnostics(string frontmatter)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        _ = new StrategyConditionCompiler(StrategyOptions.Default).Compile(definition.When!, TestSource, diagnostics);
        return diagnostics;
    }

    private static ConditionExpressionCondition Compile(
        string frontmatter,
        IStrategyPathRootProvider? additionalRoot = null,
        StrategyOptions? options = null)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var binder = additionalRoot is null
            ? null
            : new ConditionExpressionBinder(DefaultRoots().Concat([additionalRoot]), new JsonStrategyPathAccessorProvider());
        var condition = new StrategyConditionCompiler(options ?? StrategyOptions.Default, binder)
            .Compile(definition.When!, TestSource, diagnostics) as ConditionExpressionCondition;

        Assert.That(diagnostics.Where(d => d.Severity == StrategyDiagnosticSeverity.Error), Is.Empty);
        Assert.That(condition, Is.Not.Null);
        return condition!;
    }

    private static IReadOnlyList<IStrategyPathRootProvider> DefaultRoots() =>
    [
        new StrategyPathRootProvider<StrategyAttachmentsRoot>(
            "attachments",
            context => new StrategyAttachmentsRoot(
                context.Attachments.OfType<TextSelectionAttachment>().FirstOrDefault(attachment => attachment.IsPrimary) ??
                context.Attachments.OfType<TextSelectionAttachment>().FirstOrDefault(),
                context.Attachments.OfType<FileAttachment>().ToArray(),
                context.Attachments.OfType<TextAttachment>().ToArray())),
        new StrategyPathRootProvider<ProcessInfo?>("activeProcess", context => context.ActiveProcess),
        new StrategyPathRootProvider<StrategyEnvironmentRoot>("environment", _ => StrategyEnvironmentRoot.Current),
        new StrategyPathRootProvider<ExtraContextSnapshot>("extra", _ => null, isDeferred: true)
    ];

    private static StrategySource TestSource => new()
    {
        ProviderId = "user",
        Location = new Uri(@"C:\strategies\dsl.strategy.md")
    };
}

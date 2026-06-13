using System.Text.Json.Serialization;
using Everywhere.StrategyEngine;
using Everywhere.StrategyEngine.ConditionExpression;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.Core.Tests.StrategyEngine;

public class ConditionExpressionBindingTests
{
    [Test]
    public void Bind_JsonPropertyNamePathBinds()
    {
        var result = Compile(
            """
            when:
              activeProcess.name:
                equals: code
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(FindPath(result.Condition!.Bound!, "activeProcess.name"), Is.Not.Null);
        });
    }

    [Test]
    public void AccessorProvider_UsesCamelCaseFallback()
    {
        var provider = new JsonStrategyPathAccessorProvider();

        var ok = provider.TryGetAccessor(typeof(CamelCaseFallbackModel), "sampleValue", out var accessor);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(accessor.ValueType, Is.EqualTo(typeof(string)));
        });
    }

    [Test]
    public void AccessorProvider_JsonIgnoreHidesProperty()
    {
        var provider = new JsonStrategyPathAccessorProvider();

        var ok = provider.TryGetAccessor(typeof(JsonIgnoredModel), "hidden", out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void AccessorProvider_ReservedKeywordPropertyIsNotBindable()
    {
        var provider = new JsonStrategyPathAccessorProvider();

        var ok = provider.TryGetAccessor(typeof(ReservedKeywordModel), "count", out _);

        Assert.That(ok, Is.False);
    }

    [Test]
    public void Bind_UnknownRootAndUnknownSegmentAreDistinct()
    {
        var unknownRoot = Compile(
            """
            when:
              attachmnts.files:
                count:
                  min: 1
            """);
        var unknownSegment = Compile(
            """
            when:
              attachments.unknown:
                count:
                  min: 1
            """);

        Assert.Multiple(() =>
        {
            Assert.That(unknownRoot.Diagnostics.Select(d => d.Code), Does.Contain("condition.unknown_root"));
            Assert.That(unknownSegment.Diagnostics.Select(d => d.Code), Does.Contain("condition.segment_missing"));
        });
    }

    [Test]
    public void Bind_AttachmentsSelectionTextLengthBinds()
    {
        var result = Compile(
            """
            when:
              attachments.selection.text:
                length:
                  min: 1
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(FindPath(result.Condition!.Bound!, "attachments.selection.text")!.ValueType.Kind, Is.EqualTo(ConditionValueKind.String));
        });
    }

    [Test]
    public void Bind_AttachmentsFilesCountBinds()
    {
        var result = Compile(
            """
            when:
              attachments.files:
                count:
                  min: 1
            """);

        Assert.Multiple(() =>
        {
            Assert.That(result.Diagnostics, Is.Empty);
            Assert.That(FindPath(result.Condition!.Bound!, "attachments.files")!.ValueType.Kind, Is.EqualTo(ConditionValueKind.Collection));
        });
    }

    [Test]
    public void Bind_AttachmentsFilesAnyExtensionInBinds()
    {
        var result = Compile(
            """
            when:
              attachments.files:
                any:
                  extension:
                    in: [".md", ".txt"]
            """);

        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void Bind_ObjectCollectionContainsIsRejected()
    {
        var result = Compile(
            """
            when:
              attachments.files:
                contains:
                  path: a.md
            """);

        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.type_mismatch"));
    }

    [Test]
    public void Bind_ScalarCollectionContainsAnyAndAllValidate()
    {
        var result = Compile(
            """
            when:
              tags:
                containsAny: ["image"]
                containsAll: ["text", "image"]
            """,
            new StrategyPathRootProvider<IReadOnlyList<string>>("tags", _ => new[] { "text", "image" }));

        Assert.That(result.Diagnostics, Is.Empty);
    }

    [Test]
    public void Bind_StringOperatorOnCollectionIsRejected()
    {
        var result = Compile(
            """
            when:
              attachments.files:
                startsWith: a
            """);

        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("condition.type_mismatch"));
    }

    [Test]
    public void Bind_InvalidRegexProducesDiagnostic()
    {
        var result = Compile(
            """
            when:
              attachments.selection.text:
                regex: "["
            """);

        Assert.That(result.Diagnostics.Select(d => d.Code), Does.Contain("regex.invalid"));
    }

    [Test]
    public void Bind_IndexAndRangeRequireCollections()
    {
        var valid = Compile(
            """
            when:
              attachments.files[0].path:
                glob: "*.md"
            """);
        var invalid = Compile(
            """
            when:
              attachments.selection[0].text:
                equals: a
            """);

        Assert.Multiple(() =>
        {
            Assert.That(valid.Diagnostics, Is.Empty);
            Assert.That(invalid.Diagnostics.Select(d => d.Code), Does.Contain("condition.collection_required"));
        });
    }

    [Test]
    public void Bind_LogicalOperatorsProduceConcreteBoundNodes()
    {
        var all = Compile(
            """
            when:
              all:
                - attachments.files:
                    count:
                      min: 1
            """);
        var any = Compile(
            """
            when:
              any:
                - attachments.files:
                    count:
                      min: 1
            """);
        var none = Compile(
            """
            when:
              none:
                - attachments.files:
                    count:
                      min: 1
            """);

        Assert.Multiple(() =>
        {
            Assert.That(all.Condition!.Bound, Is.TypeOf<ConditionAllNode>());
            Assert.That(any.Condition!.Bound, Is.TypeOf<ConditionAnyNode>());
            Assert.That(none.Condition!.Bound, Is.TypeOf<ConditionNoneNode>());
        });
    }

    [Test]
    public void Bind_OperatorsProduceConcreteBoundOperatorTypes()
    {
        var regex = Compile(
            """
            when:
              attachments.selection.text:
                regex: "^a"
            """);
        var count = Compile(
            """
            when:
              attachments.files:
                count:
                  min: 1
            """);
        var any = Compile(
            """
            when:
              attachments.files:
                any:
                  extension:
                    equals: ".md"
            """);

        Assert.Multiple(() =>
        {
            Assert.That(FindPath(regex.Condition!.Bound!, "attachments.selection.text")!.Operators.Single(), Is.TypeOf<RegexConditionOperator>());
            Assert.That(FindPath(count.Condition!.Bound!, "attachments.files")!.Operators.Single(), Is.TypeOf<CountConditionOperator>());
            Assert.That(FindPath(any.Condition!.Bound!, "attachments.files")!.Operators.Single(), Is.TypeOf<AnyCollectionConditionOperator>());
        });
    }

    private static CompileResult Compile(string frontmatter, params IStrategyPathRootProvider[] additionalRoots)
    {
        var document = StrategyDocumentParser.Parse(
            @"C:\strategies\dsl.strategy.md",
            $"---\n{frontmatter}\n---\nBody.",
            "user");
        var definition = (StrategyDefinitionV1)document.Definition;
        var diagnostics = new List<StrategyDiagnostic>(document.Diagnostics);
        var roots = DefaultRoots().Concat(additionalRoots).ToArray();
        var binder = new ConditionExpressionBinder(roots, new JsonStrategyPathAccessorProvider());
        var condition = new StrategyConditionCompiler(StrategyOptions.Default, binder)
            .Compile(definition.When!, TestSource, diagnostics) as ConditionExpressionCondition;
        return new CompileResult(condition, diagnostics);
    }

    private static IReadOnlyList<IStrategyPathRootProvider> DefaultRoots() =>
    [
        new StrategyPathRootProvider<StrategyAttachmentsRoot>("attachments", _ => null),
        new StrategyPathRootProvider<ProcessInfo>("activeProcess", _ => null),
        new StrategyPathRootProvider<StrategyEnvironmentRoot>("environment", _ => StrategyEnvironmentRoot.Current),
        new StrategyPathRootProvider<ExtraContextSnapshot>("extra", _ => null, isDeferred: true)
    ];

    private static ConditionPathNode? FindPath(ConditionNode node, string path) =>
        node switch
        {
            ConditionPathNode pathNode when pathNode.PublicPath == path => pathNode,
            ConditionImplicitAllNode all => all.Children.Select(child => FindPath(child, path)).FirstOrDefault(match => match is not null),
            ConditionChildrenNode children => children.Children.Select(child => FindPath(child, path)).FirstOrDefault(match => match is not null),
            ConditionNotNode not => FindPath(not.Inner, path),
            _ => null
        };

    private static StrategySource TestSource => new()
    {
        ProviderId = "user",
        Location = new Uri(@"C:\strategies\dsl.strategy.md")
    };

    private sealed record CompileResult(ConditionExpressionCondition? Condition, IReadOnlyList<StrategyDiagnostic> Diagnostics);

    private sealed record CamelCaseFallbackModel(string SampleValue);

    private sealed record JsonIgnoredModel([property: JsonIgnore] string Hidden);

    private sealed record ReservedKeywordModel(int Count);

}

using System.Diagnostics;
using System.Text;
using Everywhere.StrategyEngine.ConditionExpression.PathBinding;
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.StrategyEngine.ConditionExpression;

internal sealed class ConditionCompilation
{
    private readonly IReadOnlyDictionary<string, IStrategyPathRootProvider> _roots;
    private readonly StrategyOptions _options;
    private readonly StrategySource _source;
    private readonly ExtraContextProviderPipeline? _extraContext;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _extraRequiredPaths;

    public ConditionCompilation(
        ConditionSyntaxNode syntax,
        ConditionNode? boundRoot,
        IReadOnlyDictionary<string, IStrategyPathRootProvider> roots,
        StrategyOptions options,
        StrategySource source,
        ExtraContextProviderPipeline? extraContext = null)
    {
        Syntax = syntax;
        BoundRoot = boundRoot;
        _roots = roots;
        _options = options;
        _source = source;
        _extraContext = extraContext;

        CanonicalSyntax = syntax.ToCanonicalString();
        Requirements = ConditionRequirementAnalyzer.Analyze(boundRoot, out _extraRequiredPaths);
        StaticDiagnostics = ConditionStaticAnalyzer.Analyze(boundRoot, roots, source);
        Explain = ConditionExplainBuilder.Create(CanonicalSyntax, boundRoot, StaticDiagnostics);
    }

    public ConditionSyntaxNode Syntax { get; }

    public ConditionNode? BoundRoot { get; }

    public string CanonicalSyntax { get; }

    public IReadOnlyList<StrategyDiagnostic> StaticDiagnostics { get; }

    public StrategyContextRequirements Requirements { get; }

    public ConditionExplainPlan Explain { get; }

    public ConditionEvaluationResult EvaluateDetailed(StrategyContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var diagnostics = new List<StrategyDiagnostic>(StaticDiagnostics);
        var extraResolver = new ExtraContextPathResolver(_extraContext, _extraRequiredPaths);
        var pathResolver = new ConditionPathResolver(_roots, extraResolver);
        var evaluation = new ConditionEvaluationContext(context, _options, _source, diagnostics, pathResolver);
        var value = BoundRoot?.Evaluate(evaluation);
        stopwatch.Stop();

        return new ConditionEvaluationResult
        {
            Value = value,
            Path = "when",
            Duration = stopwatch.Elapsed,
            Diagnostics = diagnostics
        };
    }
}

internal sealed class ConditionRequirementAnalyzer : IConditionVisitor
{
    private readonly Dictionary<string, SortedSet<string>> _extraPaths = new(StringComparer.Ordinal);

    public static StrategyContextRequirements Analyze(
        ConditionNode? node,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> extraRequiredPaths)
    {
        var analyzer = new ConditionRequirementAnalyzer();
        node?.Accept(analyzer);
        extraRequiredPaths = analyzer._extraPaths.AsValueEnumerable().ToDictionary(
            pair => pair.Key,
            IReadOnlyList<string> (pair) => pair.Value.AsValueEnumerable().ToList(),
            StringComparer.Ordinal);
        return new StrategyContextRequirements
        {
            ExtraRoots = analyzer._extraPaths.Keys.AsValueEnumerable().ToHashSet(StringComparer.Ordinal)
        };
    }

    public void Visit(ConditionScalarNode node) { }

    public void Visit(ConditionChildrenNode node) => this.VisitChildren(node.Children);

    public void Visit(ConditionNotNode node) => node.Inner.Accept(this);

    public void Visit(ConditionPathNode node)
    {
        if (node.PublicPath.StartsWith("extra.", StringComparison.Ordinal))
        {
            var parts = node.PublicPath.Split('.', 3);
            if (parts.Length >= 2)
            {
                var publicRoot = $"extra.{parts[1]}";
                if (!_extraPaths.TryGetValue(publicRoot, out var paths))
                {
                    paths = new SortedSet<string>(StringComparer.Ordinal);
                    _extraPaths[publicRoot] = paths;
                }

                paths.Add(node.PublicPath);
            }
        }

        foreach (var child in node.Operators
                     .AsValueEnumerable()
                     .OfType<CollectionPredicateConditionOperator>()
                     .SelectMany(op => op.PredicateChildren))
        {
            child.Accept(this);
        }

        foreach (var child in node.Operators.AsValueEnumerable().OfType<NotConditionOperator>().SelectMany(op => op.PredicateChildren))
        {
            child.Accept(this);
        }
    }
}

internal sealed class ConditionStaticAnalyzer(
    IReadOnlyDictionary<string, IStrategyPathRootProvider> roots,
    StrategySource source
) : IConditionVisitor
{
    private readonly List<StrategyDiagnostic> _diagnostics = [];

    public static IReadOnlyList<StrategyDiagnostic> Analyze(
        ConditionNode? node,
        IReadOnlyDictionary<string, IStrategyPathRootProvider> roots,
        StrategySource source)
    {
        var analyzer = new ConditionStaticAnalyzer(roots, source);
        node?.Accept(analyzer);
        return analyzer._diagnostics;
    }

    public void Visit(ConditionScalarNode node)
    {
        if (node.Value is bool)
        {
            Add(StrategyDiagnosticSeverity.Warning, "condition.constant_expression", $"Condition is constant '{node.Value}'.", node.Path);
        }
    }

    public void Visit(ConditionChildrenNode node) => this.VisitChildren(node.Children);

    public void Visit(ConditionNotNode node) => node.Inner.Accept(this);

    public void Visit(ConditionPathNode node)
    {
        if (!node.PublicPath.StartsWith("$item", StringComparison.Ordinal))
        {
            var rootName = node.PublicPath.Split('.', 2)[0];
            if (roots.TryGetValue(rootName, out var root) && root.IsDeferred)
            {
                Add(StrategyDiagnosticSeverity.Info, "condition.deferred_root", $"Condition root '{root.RootName}' is deferred.", node.Path);
            }
        }

        foreach (var op in node.Operators)
        {
            if (op is ScalarOperandConditionOperator scalar &&
                (op.OperatorName == "containsAny" || op.OperatorName == "containsAll") &&
                ConditionRuntimeValues.TryMaterializeCollection(scalar.Operand, out var items) &&
                items.Count == 0)
            {
                Add(
                    StrategyDiagnosticSeverity.Warning,
                    op.OperatorName == "containsAny" ? "condition.empty_contains_any" : "condition.empty_contains_all",
                    $"Operator '{op.OperatorName}' has an empty operand.",
                    op.NodePath);
            }

            foreach (var child in op switch
                     {
                         CollectionPredicateConditionOperator predicate => predicate.PredicateChildren,
                         NotConditionOperator not => not.PredicateChildren,
                         _ => []
                     })
            {
                child.Accept(this);
            }
        }
    }

    private void Add(StrategyDiagnosticSeverity severity, string code, string message, string path) =>
        _diagnostics.Add(
            new StrategyDiagnostic
            {
                Severity = severity,
                Code = code,
                MessageKey = new DirectResourceKey(message),
                Path = path,
                ProviderId = source.ProviderId
            });
}

internal sealed record ConditionExplainPlan(
    string CanonicalSyntax,
    IReadOnlyList<ConditionExplainPath> Paths,
    IReadOnlyList<StrategyDiagnostic> StaticDiagnostics,
    string Text
);

internal sealed record ConditionExplainPath(
    string PublicPath,
    string StaticType,
    IReadOnlyList<string> Operators
);

internal sealed class ConditionExplainBuilder : IConditionVisitor
{
    private readonly List<ConditionExplainPath> _paths = [];

    public static ConditionExplainPlan Create(
        string canonicalSyntax,
        ConditionNode? bound,
        IReadOnlyList<StrategyDiagnostic> staticDiagnostics)
    {
        var builder = new ConditionExplainBuilder();
        bound?.Accept(builder);
        var paths = builder._paths.ToArray();
        return new ConditionExplainPlan(
            canonicalSyntax,
            paths,
            staticDiagnostics,
            BuildText(canonicalSyntax, paths, staticDiagnostics));
    }

    public void Visit(ConditionScalarNode node) { }

    public void Visit(ConditionChildrenNode node) => this.VisitChildren(node.Children);

    public void Visit(ConditionNotNode node) => node.Inner.Accept(this);

    public void Visit(ConditionPathNode node)
    {
        _paths.Add(
            new ConditionExplainPath(
                node.PublicPath,
                node.ValueType.ToString(),
                node.Operators
                    .Where(op => op is not CaseSensitiveConditionOperator)
                    .Select(op => op.OperatorName)
                    .ToArray()));

        foreach (var child in node.Operators.OfType<CollectionPredicateConditionOperator>().SelectMany(op => op.PredicateChildren))
        {
            child.Accept(this);
        }

        foreach (var child in node.Operators.OfType<NotConditionOperator>().SelectMany(op => op.PredicateChildren))
        {
            child.Accept(this);
        }
    }

    private static string BuildText(
        string canonicalSyntax,
        IReadOnlyList<ConditionExplainPath> paths,
        IReadOnlyList<StrategyDiagnostic> staticDiagnostics)
    {
        var builder = new StringBuilder();
        builder.AppendLine("canonical:");
        foreach (var line in canonicalSyntax.Split('\n'))
        {
            builder.Append("  ").AppendLine(line);
        }

        builder.AppendLine("paths:");
        foreach (var path in paths.OrderBy(path => path.PublicPath, StringComparer.Ordinal))
        {
            builder.Append("  ")
                .Append(path.PublicPath)
                .Append(" : ")
                .Append(path.StaticType)
                .Append(" [")
                .Append(string.Join(", ", path.Operators))
                .AppendLine("]");
        }

        builder.AppendLine("diagnostics:");
        foreach (var diagnostic in staticDiagnostics.OrderBy(diagnostic => diagnostic.Code, StringComparer.Ordinal))
        {
            builder.Append("  ").Append(diagnostic.Code).Append(" @ ").AppendLine(diagnostic.Path ?? string.Empty);
        }

        builder.AppendLine("null-behavior: false/null hide; true recommends");
        builder.Append("short-circuit: all(false), any(true), none(not-any)");
        return builder.ToString();
    }
}
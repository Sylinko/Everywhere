using Everywhere.StrategyEngine.ConditionExpression.PathBinding;

namespace Everywhere.StrategyEngine.ConditionExpression.Syntax;

/// <summary>
/// Semantic condition node produced after DSL binding.
/// </summary>
/// <remarks>
/// Bound nodes are executable and no longer interpret DSL keywords by name. Non-execution concerns such as
/// requirement inference and explain output should traverse nodes through <see cref="Accept"/>.
/// </remarks>
internal abstract class ConditionNode(string path)
{
    /// <summary>
    /// Canonical diagnostic path of the source syntax that produced this node.
    /// </summary>
    /// <example><c>when.attachments.files.any.extension.in</c></example>
    public string Path { get; } = path;

    /// <summary>
    /// Evaluates this node using three-valued condition semantics.
    /// </summary>
    public abstract bool? Evaluate(ConditionEvaluationContext context);

    /// <summary>
    /// Dispatches this node to a visitor for non-runtime traversals.
    /// </summary>
    public abstract void Accept(IConditionVisitor visitor);
}

/// <summary>
/// Bound literal condition value.
/// </summary>
/// <remarks>
/// Root boolean literals are executable conditions. Other scalar values are preserved so the evaluator can emit
/// a runtime mismatch instead of losing source context.
/// </remarks>
internal sealed class ConditionScalarNode(
    string scalarPath,
    object? value,
    ConditionValueType valueType
) : ConditionNode(scalarPath)
{
    public object? Value { get; } = value;

    /// <summary>
    /// Static type inferred from the original scalar value.
    /// </summary>
    public ConditionValueType ValueType { get; } = valueType;

    public override bool? Evaluate(ConditionEvaluationContext context)
    {
        if (Value is bool boolean)
        {
            return boolean;
        }

        if (Value is null)
        {
            return null;
        }

        context.AddDiagnostic(
            StrategyDiagnosticSeverity.Warning,
            "condition.runtime_type_mismatch",
            "Condition scalar root must be a bool.",
            Path);
        return false;
    }

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// Base node for condition forms that evaluate a list of child conditions.
/// </summary>
internal abstract class ConditionChildrenNode(
    string nodePath,
    IReadOnlyList<ConditionNode> children
) : ConditionNode(nodePath)
{
    public IReadOnlyList<ConditionNode> Children { get; } = children;
}

/// <summary>
/// Explicit <c>all</c> logical node.
/// </summary>
internal class ConditionAllNode(
    string nodePath,
    IReadOnlyList<ConditionNode> children
) : ConditionChildrenNode(nodePath, children)
{
    public override bool? Evaluate(ConditionEvaluationContext context) =>
        ConditionLogicalSemantics.EvaluateAll(Children, context);

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// Implicit conjunction inserted when a mapping contains multiple independent paths.
/// </summary>
/// <remarks>
/// Example: <c>attachments.files.count</c> and <c>activeProcess.name</c> in the same mapping behave as an
/// <c>all</c> group, but are not authored as an explicit logical node.
/// </remarks>
internal sealed class ConditionImplicitAllNode(
    string nodePath,
    IReadOnlyList<ConditionNode> children
) : ConditionAllNode(nodePath, children);

/// <summary>
/// Explicit <c>any</c> logical node.
/// </summary>
internal sealed class ConditionAnyNode(
    string nodePath,
    IReadOnlyList<ConditionNode> children
) : ConditionChildrenNode(nodePath, children)
{
    public override bool? Evaluate(ConditionEvaluationContext context) =>
        ConditionLogicalSemantics.EvaluateAny(Children, context);

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// Explicit <c>none</c> logical node.
/// </summary>
/// <remarks>
/// This is evaluated as <c>not(any(children))</c>, preserving null according to the DSL truth table.
/// </remarks>
internal sealed class ConditionNoneNode(
    string nodePath,
    IReadOnlyList<ConditionNode> children
) : ConditionChildrenNode(nodePath, children)
{
    public override bool? Evaluate(ConditionEvaluationContext context) =>
        ConditionLogicalSemantics.Invert(ConditionLogicalSemantics.EvaluateAny(Children, context));

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// Explicit <c>not</c> logical node.
/// </summary>
internal sealed class ConditionNotNode(
    string nodePath,
    ConditionNode inner
) : ConditionNode(nodePath)
{
    public ConditionNode Inner { get; } = inner;

    public override bool? Evaluate(ConditionEvaluationContext context) =>
        ConditionLogicalSemantics.Invert(Inner.Evaluate(context));

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// Bound path access followed by zero or more typed operators.
/// </summary>
/// <remarks>
/// The path is already resolved to root/accessor/index metadata; runtime evaluation only reads values through
/// <see cref="ConditionPathResolver"/> and invokes concrete operator objects.
/// </remarks>
internal sealed class ConditionPathNode(
    string nodePath,
    string publicPath,
    ConditionValueType valueType,
    IReadOnlyList<BoundConditionPathStep> steps,
    IReadOnlyList<ConditionOperator> operators
) : ConditionNode(nodePath)
{
    /// <summary>
    /// Public DSL path visible in diagnostics and explain output.
    /// </summary>
    /// <example><c>attachments.files[0].extension</c></example>
    public string PublicPath { get; } = publicPath;

    /// <summary>
    /// Static type of the path after applying index/range tokens.
    /// </summary>
    public ConditionValueType ValueType { get; } = valueType;

    /// <summary>
    /// Runtime path operations from the root to the final value.
    /// </summary>
    public IReadOnlyList<BoundConditionPathStep> Steps { get; } = steps;

    /// <summary>
    /// Operators that must all match the resolved path value.
    /// </summary>
    public IReadOnlyList<ConditionOperator> Operators { get; } = operators;

    public override bool? Evaluate(ConditionEvaluationContext context)
    {
        var resolved = context.PathResolver.Resolve(this, context);
        if (!resolved.HasValue)
        {
            return null;
        }

        if (Operators.Count == 0)
        {
            return true;
        }

        var comparison = Operators.AsValueEnumerable().OfType<CaseSensitiveConditionOperator>().Any(op => op.IsCaseSensitive) ?
            StringComparison.Ordinal :
            StringComparison.OrdinalIgnoreCase;
        var hasNull = false;
        foreach (var op in Operators)
        {
            if (op is CaseSensitiveConditionOperator)
            {
                continue;
            }

            var value = op.Evaluate(resolved.Value, context, comparison);
            if (value is false)
            {
                return false;
            }

            hasNull |= value is null;
        }

        return hasNull ? null : true;
    }

    public override void Accept(IConditionVisitor visitor) => visitor.Visit(this);
}

/// <summary>
/// One resolved segment of a bound path.
/// </summary>
/// <remarks>
/// A step may contain only a public name when the path is under an unknown/deferred root such as <c>extra</c>.
/// For JSON-backed models, <see cref="Accessor"/> points at the compiled or reflected member accessor.
/// </remarks>
internal sealed class BoundConditionPathStep(
    string publicName,
    ConditionValueType valueType,
    StrategyPathAccessor? accessor,
    IReadOnlyList<ConditionPathToken> indexOrRangeTokens
)
{
    public string PublicName { get; } = publicName;

    /// <summary>
    /// Static type after this step and its index/range operations have been applied.
    /// </summary>
    public ConditionValueType ValueType { get; } = valueType;

    /// <summary>
    /// Member accessor for known object roots; null for deferred or unknown roots.
    /// </summary>
    public StrategyPathAccessor? Accessor { get; } = accessor;

    /// <summary>
    /// Index/range operations authored on this segment.
    /// </summary>
    /// <example><c>files[0]</c> produces one index token on the <c>files</c> step.</example>
    public IReadOnlyList<ConditionPathToken> IndexOrRangeTokens { get; } = indexOrRangeTokens;
}

/// <summary>
/// Result of resolving a path before operator evaluation.
/// </summary>
/// <remarks>
/// This distinguishes a successful resolution to <c>null</c> from a missing path. Missing paths evaluate the
/// containing condition to null and preserve diagnostics.
/// </remarks>
internal readonly record struct ConditionPathValue(bool HasValue, object? Value)
{
    public static ConditionPathValue Missing { get; } = new(false, null);
}
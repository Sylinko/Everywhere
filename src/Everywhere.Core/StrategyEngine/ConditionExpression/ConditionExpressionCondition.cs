using System.Diagnostics;
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Strategy condition backed by the structured Condition DSL pipeline.
/// </summary>
/// <remarks>
/// This type is deliberately thin: the expensive work is captured in <see cref="Compilation"/>, while each
/// evaluation creates a fresh runtime context so diagnostics, item scopes, and extra-context cache do not leak
/// across strategy evaluations.
/// </remarks>
internal sealed class ConditionExpressionCondition(object originalValue, ConditionCompilation compilation, StrategySource source) : IStrategyCondition
{
    public object OriginalValue { get; } = originalValue;

    /// <summary>
    /// Canonical frontend syntax retained for diagnostics, snapshots, and future lowering stages.
    /// </summary>
    public ConditionSyntaxNode Syntax { get; } = compilation.Syntax;

    /// <summary>
    /// Bound runtime tree, or null when binding failed and the condition was kept only for diagnostics.
    /// </summary>
    public ConditionNode? Bound { get; } = compilation.BoundRoot;

    /// <summary>
    /// Immutable compilation artifact shared by simple and detailed evaluation.
    /// </summary>
    public ConditionCompilation Compilation { get; } = compilation;

    public ConditionExplainPlan Explain => Compilation.Explain;

    public StrategySource Source { get; } = source;

    public bool? Evaluate(StrategyContext context) => Compilation.EvaluateDetailed(context).Value;

    public ConditionEvaluationResult EvaluateDetailed(StrategyContext context)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = Compilation.EvaluateDetailed(context);
        stopwatch.Stop();
        return result with
        {
            Duration = stopwatch.Elapsed
        };
    }
}

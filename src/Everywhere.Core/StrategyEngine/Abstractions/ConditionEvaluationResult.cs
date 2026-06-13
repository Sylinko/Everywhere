namespace Everywhere.StrategyEngine;

/// <summary>
/// Three-valued condition evaluation result used by the upcoming DSL evaluator.
/// </summary>
public sealed record ConditionEvaluationResult
{
    /// <summary>
    /// Three-valued result: <c>true</c>, <c>false</c>, or <c>null</c> when the condition cannot be resolved.
    /// </summary>
    public bool? Value { get; init; }

    /// <summary>
    /// Condition path that produced this result, for example <c>when.all[1]</c>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Time spent evaluating this condition node.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

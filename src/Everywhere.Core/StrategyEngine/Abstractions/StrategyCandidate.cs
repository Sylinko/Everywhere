namespace Everywhere.StrategyEngine;

/// <summary>
/// Matching result plus diagnostics for future recommendation explanation UI.
/// </summary>
public sealed record StrategyCandidate
{
    public required Strategy Strategy { get; init; }

    /// <summary>
    /// True when the strategy should be shown in normal recommendation UI.
    /// </summary>
    public required bool IsMatched { get; init; }

    /// <summary>
    /// Detailed condition result for diagnostics and explanation UI.
    /// </summary>
    public ConditionEvaluationResult? Evaluation { get; init; }

    /// <summary>
    /// Time spent evaluating this candidate.
    /// </summary>
    public TimeSpan EvaluationDuration { get; init; }

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

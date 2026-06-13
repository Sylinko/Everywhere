namespace Everywhere.StrategyEngine;

/// <summary>
/// Result of running the preprocessors declared by one strategy.
/// </summary>
public sealed record StrategyPreprocessorExecutionResult
{
    public PreprocessorResult Result { get; init; } = new();

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];

    /// <summary>
    /// True when preprocessing completed without error diagnostics.
    /// </summary>
    public bool Succeeded => Diagnostics.All(diagnostic => diagnostic.Severity != StrategyDiagnosticSeverity.Error);
}

namespace Everywhere.StrategyEngine;

/// <summary>
/// Result of compiling a strategy document into the runtime model.
/// </summary>
public sealed record StrategyNormalizationResult
{
    /// <summary>
    /// Runtime strategy when normalization succeeds without blocking errors.
    /// </summary>
    public Strategy? Strategy { get; init; }

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

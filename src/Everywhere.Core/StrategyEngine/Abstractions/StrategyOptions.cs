namespace Everywhere.StrategyEngine;

/// <summary>
/// Runtime timeout options for matching and execution.
/// </summary>
public sealed record StrategyOptions
{
    /// <summary>
    /// M1 defaults: <c>300ms / 80ms / 50ms / 120ms / 200ms</c>.
    /// </summary>
    public static StrategyOptions Default { get; } = new()
    {
        MatchingTimeout = TimeSpan.FromMilliseconds(300),
        ConditionTimeout = TimeSpan.FromMilliseconds(80),
        RegexTimeout = TimeSpan.FromMilliseconds(50),
        VisualQueryTimeout = TimeSpan.FromMilliseconds(120),
        ExtraTimeout = TimeSpan.FromMilliseconds(200),
        PreprocessorTimeout = TimeSpan.FromSeconds(2)
    };

    /// <summary>
    /// Total budget for evaluating one matching pass.
    /// </summary>
    public required TimeSpan MatchingTimeout { get; init; }

    /// <summary>
    /// Budget for evaluating a single condition node.
    /// </summary>
    public required TimeSpan ConditionTimeout { get; init; }

    /// <summary>
    /// Budget for regex-based condition checks.
    /// </summary>
    public required TimeSpan RegexTimeout { get; init; }

    /// <summary>
    /// Budget for visual query checks.
    /// </summary>
    public required TimeSpan VisualQueryTimeout { get; init; }

    /// <summary>
    /// Budget for on-demand extra context collection.
    /// </summary>
    public required TimeSpan ExtraTimeout { get; init; }

    /// <summary>
    /// Budget for each execution-time preprocessor.
    /// </summary>
    public required TimeSpan PreprocessorTimeout { get; init; }
}

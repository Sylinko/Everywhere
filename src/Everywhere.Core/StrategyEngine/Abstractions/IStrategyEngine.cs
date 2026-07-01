namespace Everywhere.StrategyEngine;

/// <summary>
/// The main entry point for the Strategy Engine.
/// Orchestrates strategy matching and command generation.
/// </summary>
public interface IStrategyEngine
{
    /// <summary>
    /// The strategy registry containing all available strategies.
    /// </summary>
    IStrategyRegistry Registry { get; }

    /// <summary>
    /// Evaluates all strategies against the current context and returns matching ones.
    /// Deduplicated and sorted by priority.
    /// </summary>
    /// <param name="context">The strategy context to evaluate.</param>
    /// <returns>List of matching strategies, sorted by priority (descending).</returns>
    IReadOnlyList<Strategy> GetStrategies(StrategyContext context);
}
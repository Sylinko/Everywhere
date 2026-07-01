namespace Everywhere.StrategyEngine;

/// <summary>
/// Provides a collection of strategies.
/// Implementations can load strategies from files, databases, or memory.
/// </summary>
public interface IStrategyProvider
{
    /// <summary>
    /// The namespace this provider contributes to (e.g., "builtin", "user").
    /// </summary>
    string Namespace { get; }

    /// <summary>
    /// Gets all strategies available from this provider.
    /// </summary>
    /// <returns>An enumeration of strategies.</returns>
    IEnumerable<Strategy> GetStrategies();
}
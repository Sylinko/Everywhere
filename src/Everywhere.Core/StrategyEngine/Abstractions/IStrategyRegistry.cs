namespace Everywhere.StrategyEngine;

/// <summary>
/// Registry responsible for collecting and deduplicating strategies from all providers.
/// </summary>
public interface IStrategyRegistry
{
    /// <summary>
    /// Gets all registered strategy wrappers, deduplicated by fully qualified Id and keeping the highest priority ones.
    /// </summary>
    IEnumerable<Strategy> GetRegisteredStrategies();
}
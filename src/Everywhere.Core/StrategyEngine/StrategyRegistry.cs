namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of the strategy registry that collects strategies from DI-injected providers.
/// </summary>
public sealed class StrategyRegistry(IEnumerable<IStrategyProvider> providers) : IStrategyRegistry
{
    public IEnumerable<Strategy> GetRegisteredStrategies() =>
        providers.SelectMany(p => p.GetStrategies().Select(s => s with
        {
            Id = $"{p.Namespace}.{s.Id}"
        }));
}
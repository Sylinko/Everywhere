namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of the strategy registry that collects strategies from DI-injected providers.
/// </summary>
public sealed class StrategyRegistry(IEnumerable<IStrategyProvider> providers) : IStrategyRegistry
{
    public IEnumerable<Strategy> GetRegisteredStrategies() =>
        providers.SelectMany(p => p.GetStrategies().Select(s => NormalizeProviderStrategy(p, s)));

    private static Strategy NormalizeProviderStrategy(IStrategyProvider provider, Strategy strategy)
    {
        if (strategy.Id.StartsWith($"{provider.Namespace}.", StringComparison.Ordinal))
        {
            return strategy.Source.ProviderId.Equals(provider.Namespace, StringComparison.Ordinal)
                ? strategy
                : strategy with { Source = strategy.Source with { ProviderId = provider.Namespace } };
        }

        return strategy with
        {
            Id = $"{provider.Namespace}.{strategy.Id}",
            Source = StrategySource.FromProvider(provider.Namespace, strategy.Id)
        };
    }
}

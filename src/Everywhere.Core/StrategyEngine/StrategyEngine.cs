using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.StrategyEngine;

/// <summary>
/// Default implementation of <see cref="IStrategyEngine"/>.
/// Orchestrates strategy collection and matching.
/// </summary>
public sealed class StrategyEngine(IStrategyRegistry registry, ILogger<StrategyEngine> logger) : IStrategyEngine
{
    public IStrategyRegistry Registry { get; } = registry;

    public IReadOnlyList<Strategy> GetStrategies(StrategyContext context)
    {
        var results = new List<Strategy>();
        foreach (var strategy in Registry.GetRegisteredStrategies())
        {
            try
            {
                if (strategy.Condition?.Evaluate(context) is not false) results.Add(strategy);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Error evaluating condition for strategy {StrategyId}", strategy.Id);
            }
        }

        return results.AsValueEnumerable().OrderByDescending(s => s.Priority).DistinctBy(s => s.Id).ToList();
    }
}

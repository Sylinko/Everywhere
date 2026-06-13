namespace Everywhere.StrategyEngine;

public interface IStrategyDefinitionNormalizer
{
    /// <summary>
    /// Schema handled by this normalizer, for example <c>everywhere.strategy/v1</c>.
    /// </summary>
    string Schema { get; }

    Task<StrategyNormalizationResult> NormalizeAsync(
        StrategyDocument document,
        StrategyLoadContext context,
        CancellationToken cancellationToken);
}

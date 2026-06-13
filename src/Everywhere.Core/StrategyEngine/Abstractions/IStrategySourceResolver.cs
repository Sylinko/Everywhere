namespace Everywhere.StrategyEngine;

public interface IStrategySourceResolver
{
    bool CanResolve(StrategyFromReference reference, StrategySource currentSource);

    Task<StrategyDocument> ResolveAsync(StrategyFromReference reference, StrategySource currentSource, CancellationToken cancellationToken);
}

namespace Everywhere.StrategyEngine;

public abstract class BuiltInStrategyProvider(string id) : IStrategyProvider
{
    public string Id { get; } = id;

    public string Namespace => "builtin";

    public abstract IEnumerable<Strategy> GetStrategies();
}

namespace Everywhere.StrategyEngine;

public abstract class BuiltInStrategyProvider : IStrategyProvider
{
    public string Namespace => "builtin";

    public abstract IEnumerable<Strategy> GetStrategies();
}
namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Negates another condition.
/// </summary>
public sealed class NotCondition : IStrategyCondition
{
    public required IStrategyCondition Inner { get; init; }

    public bool Evaluate(StrategyContext context) => !Inner.Evaluate(context);
}
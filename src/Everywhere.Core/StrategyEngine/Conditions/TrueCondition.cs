namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// A condition that always matches. Useful for global strategies.
/// </summary>
public sealed class TrueCondition : IStrategyCondition
{
    public static TrueCondition Shared { get; } = new();

    public bool Evaluate(StrategyContext context) => true;
}
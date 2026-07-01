namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// A condition that never matches. Useful for disabled strategies.
/// </summary>
public sealed class FalseCondition : IStrategyCondition
{
    public static FalseCondition Shared { get; } = new();

    public bool Evaluate(StrategyContext context) => false;
}
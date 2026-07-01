using ZLinq;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Combines multiple conditions with AND or OR logic.
/// </summary>
public sealed class CompositeCondition : IStrategyCondition
{
    /// <summary>
    /// The logic operator for combining conditions.
    /// </summary>
    public CompositeLogic Logic { get; init; } = CompositeLogic.And;

    /// <summary>
    /// The conditions to combine.
    /// </summary>
    public required IReadOnlyList<IStrategyCondition> Conditions { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        if (Conditions.Count == 0)
        {
            return true;
        }

        return Logic switch
        {
            CompositeLogic.And => Conditions.AsValueEnumerable().All(c => c.Evaluate(context)),
            CompositeLogic.Or => Conditions.AsValueEnumerable().Any(c => c.Evaluate(context)),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    /// <summary>
    /// Creates an AND composite condition.
    /// </summary>
    public static CompositeCondition And(params IStrategyCondition[] conditions) => new()
    {
        Logic = CompositeLogic.And,
        Conditions = conditions
    };

    /// <summary>
    /// Creates an OR composite condition.
    /// </summary>
    public static CompositeCondition Or(params IStrategyCondition[] conditions) => new()
    {
        Logic = CompositeLogic.Or,
        Conditions = conditions
    };
}

/// <summary>
/// Logic operators for combining conditions.
/// </summary>
public enum CompositeLogic
{
    /// <summary>
    /// All conditions must match.
    /// </summary>
    And,

    /// <summary>
    /// Any condition can match.
    /// </summary>
    Or
}
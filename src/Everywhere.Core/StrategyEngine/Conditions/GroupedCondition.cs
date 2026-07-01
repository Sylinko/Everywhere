using ZLinq;

namespace Everywhere.StrategyEngine.Conditions;

/// <summary>
/// Condition groups with OR logic between groups and AND logic within groups.
/// This matches the YAML configuration model.
/// </summary>
public sealed class GroupedCondition : IStrategyCondition
{
    /// <summary>
    /// Groups of conditions. Strategy matches if ANY group matches.
    /// Within a group, ALL conditions must match.
    /// </summary>
    public required IReadOnlyList<IReadOnlyList<IStrategyCondition>> Groups { get; init; }

    public bool Evaluate(StrategyContext context)
    {
        // OR between groups, AND within groups
        return Groups.AsValueEnumerable().Any(group => group.AsValueEnumerable().All(c => c.Evaluate(context)));
    }
}
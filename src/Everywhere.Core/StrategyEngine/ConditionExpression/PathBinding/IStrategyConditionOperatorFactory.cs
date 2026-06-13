namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Marker for future pluggable operator factories.
/// </summary>
/// <remarks>
/// The current binder is hand-written, but this keeps the shape open for provider-owned or generated operator
/// registrations without changing the bound runtime.
/// </remarks>
internal interface IStrategyConditionOperatorFactory
{
    string OperatorName { get; }
}
using Everywhere.StrategyEngine.ConditionExpression.Syntax;

namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Binds an operator name and operand syntax to a concrete bound operator.
/// </summary>
/// <remarks>
/// String matching of DSL operator names is intentionally limited to this binding stage. Runtime evaluation uses
/// the concrete <see cref="ConditionOperator"/> instance.
/// </remarks>
internal interface IStrategyConditionOperatorBinder
{
    bool IsOperator(string name);

    ConditionOperator? BindOperator(
        string op,
        ConditionExpressionBinder.BindingScope scope,
        ConditionSyntaxNode operand,
        ConditionExpressionBinder binder,
        StrategySource source,
        List<StrategyDiagnostic> diagnostics);
}
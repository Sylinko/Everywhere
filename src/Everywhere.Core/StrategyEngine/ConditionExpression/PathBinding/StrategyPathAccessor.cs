namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Runtime accessor for one bindable DSL property.
/// </summary>
/// <remarks>
/// <see cref="PublicName"/> is the JSON/DSL name, while <see cref="DeclaringType"/> and <see cref="ValueType"/>
/// describe the CLR member that supplies the value.
/// </remarks>
/// <param name="PublicName">Name accepted in strategy DSL paths.</param>
/// <param name="DeclaringType">CLR type that owns the accessor.</param>
/// <param name="ValueType">CLR type returned by the accessor.</param>
/// <param name="GetValue">Runtime read delegate for the exposed member.</param>
internal sealed record StrategyPathAccessor(
    string PublicName,
    Type DeclaringType,
    Type ValueType,
    Func<object, object?> GetValue);
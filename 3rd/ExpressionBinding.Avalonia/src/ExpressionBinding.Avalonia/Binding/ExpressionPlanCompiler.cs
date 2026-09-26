using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia.Binding;

internal sealed class ExpressionPlanCompiler
{
    private static readonly MethodInfo GetValueMethod = ((Func<IList<object?>, int, object?>)GetValue).Method;
    private static readonly MethodInfo InvokeBooleanConversionMethod =
        ((Func<CallSite<BooleanConversionSiteTarget>, object?, bool>)InvokeBooleanConversion).Method;
    private static readonly MethodInfo ThrowNoSwitchMatchMethod = ((Func<object>)ThrowNoSwitchMatch).Method;

    private readonly ParameterExpression _values = Expression.Parameter(typeof(IList<object?>), "values");
    private readonly ExpressionRegistry _registry;

    private ExpressionPlanCompiler(ExpressionRegistry registry)
    {
        _registry = registry;
    }

    internal static Func<IList<object?>, object?> Compile(ExpressionSyntax syntax, ExpressionRegistry registry)
    {
        var compiler = new ExpressionPlanCompiler(registry);
        var body = compiler.Build(syntax).Expression;
        var lambda = Expression.Lambda<Func<IList<object?>, object?>>(body, compiler._values);
        return lambda.Compile(preferInterpretation: !RuntimeFeature.IsDynamicCodeSupported);
    }

    private PlanValue Build(ExpressionSyntax syntax) => syntax switch
    {
        NumberSyntax number => BuildNumber(number),
        StringSyntax @string => PlanValue.FromValue(@string.Value),
        LiteralSyntax literal => BuildLiteral(literal),
        IdentifierSyntax identifier => BuildIdentifier(identifier),
        CallSyntax call => BuildCall(call),
        UnarySyntax unary => BuildUnary(unary),
        BinarySyntax binary => BuildBinary(binary),
        ConditionalSyntax conditional => BuildConditional(conditional),
        SwitchSyntax @switch => BuildSwitch(@switch),
        _ => throw new ExpressionBindingException($"Unsupported syntax node '{syntax.GetType().Name}'.")
    };

    private static PlanValue BuildNumber(NumberSyntax number) =>
        new(Box(ExpressionBinder.MaterializeNumericLiteral(number.Value)), DynamicArgumentInfo.FromNumericLiteral(number.Value));

    private static PlanValue BuildLiteral(LiteralSyntax literal) => literal.Kind switch
    {
        LiteralKind.False => PlanValue.FromValue(false),
        LiteralKind.True => PlanValue.FromValue(true),
        LiteralKind.Null => PlanValue.FromValue(null),
        LiteralKind.Unset => PlanValue.FromValue(AvaloniaProperty.UnsetValue),
        _ => throw new ExpressionBindingException($"Unsupported literal '{literal.Kind}'.")
    };

    private PlanValue BuildIdentifier(IdentifierSyntax identifier)
    {
        if (identifier.Name is not [var name] || name is < 'A' or > 'Z')
        {
            throw new ExpressionBindingException($"Unknown identifier '{identifier.Name}'. Arguments are addressed by A through Z.");
        }

        var index = name - 'A';
        var item = Expression.Call(GetValueMethod, _values, Expression.Constant(index));
        return new PlanValue(item, DynamicArgumentInfo.Value);
    }

    private PlanValue BuildCall(CallSyntax call)
    {
        var arguments = call.Arguments.Select(Build).ToArray();
        var argumentInfo = arguments.Select(static argument => argument.ArgumentInfo).ToArray();
        var key = DynamicBinderKey.Function(call.Name, argumentInfo);
        var binder = _registry.GetOrAddBinder(key, () => new FunctionCallDynamicBinder(_registry, call.Name, argumentInfo));
        return PlanValue.FromExpression(BuildDynamic(binder, arguments.Select(static argument => argument.Expression)));
    }

    private PlanValue BuildUnary(UnarySyntax unary)
    {
        var operand = Build(unary.Operand);
        if (operand.ArgumentInfo.IsNumericLiteral && unary.Operator is UnaryOperator.Plus or UnaryOperator.Negate)
        {
            var value = unary.Operator == UnaryOperator.Negate ? -operand.ArgumentInfo.NumericLiteral : operand.ArgumentInfo.NumericLiteral;
            return new PlanValue(Box(ExpressionBinder.MaterializeNumericLiteral(value)), DynamicArgumentInfo.FromNumericLiteral(value));
        }

        var key = DynamicBinderKey.Unary(unary.Operator, operand.ArgumentInfo);
        var binder = _registry.GetOrAddBinder(key, () => new UnaryOperationDynamicBinder(unary.Operator, operand.ArgumentInfo));
        return PlanValue.FromExpression(BuildDynamic(binder, [operand.Expression]));
    }

    private PlanValue BuildBinary(BinarySyntax binary)
    {
        if (binary.Operator is BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr)
        {
            return PlanValue.FromExpression(BuildShortCircuit(binary));
        }

        var left = Build(binary.Left);
        var right = Build(binary.Right);
        return BuildDynamicBinary(binary.Operator, left, right);
    }

    private PlanValue BuildDynamicBinary(BinaryOperator @operator, PlanValue left, PlanValue right)
    {
        var key = DynamicBinderKey.Binary(@operator, left.ArgumentInfo, right.ArgumentInfo);
        var binder = _registry.GetOrAddBinder(key, () => new BinaryOperationDynamicBinder(@operator, left.ArgumentInfo, right.ArgumentInfo));
        return PlanValue.FromExpression(BuildDynamic(binder, [left.Expression, right.Expression]));
    }

    private PlanValue BuildConditional(ConditionalSyntax conditional)
    {
        var condition = Build(conditional.Condition);
        var whenTrue = Build(conditional.WhenTrue).Expression;
        var whenFalse = Build(conditional.WhenFalse).Expression;
        return PlanValue.FromExpression(BuildConditionalValue(condition, whenTrue, whenFalse));
    }

    private PlanValue BuildSwitch(SwitchSyntax @switch)
    {
        var input = Build(@switch.Input).Expression;
        var inputValue = Expression.Variable(typeof(object), "switchInput");
        Expression body = Expression.Call(ThrowNoSwitchMatchMethod);

        foreach (var arm in @switch.Arms.Reverse())
        {
            var pattern = BuildPattern(inputValue, arm.Patterns);
            var fallback = body;
            var result = Build(arm.Result).Expression;
            if (arm.Guard is not null)
            {
                var guard = Build(arm.Guard);
                var guardValue = Expression.Variable(typeof(object), "switchGuard");
                var guardPlan = guard with { Expression = guardValue };
                result = Expression.Block(
                    [guardValue],
                    Expression.Assign(guardValue, guard.Expression),
                    Expression.Condition(
                        IsUnset(guardValue),
                        Unset(),
                        Expression.Condition(
                            BuildBooleanConversion(guardPlan),
                            result,
                            fallback)));
            }

            body = Expression.Condition(pattern, result, fallback);
        }

        return PlanValue.FromExpression(Expression.Block([inputValue], Expression.Assign(inputValue, input), body));
    }

    private Expression BuildPattern(Expression input, IReadOnlyList<SwitchPatternSyntax> patterns)
    {
        if (patterns.Count == 0)
        {
            throw new ExpressionBindingException("A switch arm must contain at least one pattern.");
        }

        Expression? result = null;
        foreach (var pattern in patterns)
        {
            var next = BuildPattern(input, pattern);
            result = result is null ? next : Expression.OrElse(result, next);
        }

        return result ?? throw new InvalidOperationException("Switch pattern construction produced no condition.");
    }

    private Expression BuildPattern(Expression input, SwitchPatternSyntax pattern)
    {
        if (pattern is DiscardPatternSyntax)
        {
            return Expression.Constant(true);
        }

        var (operation, value) = pattern switch
        {
            ConstantPatternSyntax constant => (BinaryOperator.Equal, constant.Value),
            RelationalPatternSyntax relational => (relational.Operator, relational.Value),
            _ => throw new ExpressionBindingException($"Unsupported switch pattern '{pattern.GetType().Name}'.")
        };
        var comparison = BuildDynamicBinary(operation, PlanValue.FromExpression(input), Build(value));
        var comparisonValue = Expression.Variable(typeof(object), "patternResult");
        return Expression.Block(
            [comparisonValue],
            Expression.Assign(comparisonValue, comparison.Expression),
            Expression.Condition(
                IsUnset(comparisonValue),
                Expression.Constant(false),
                BuildBooleanConversion(PlanValue.FromExpression(comparisonValue))));
    }

    private Expression BuildShortCircuit(BinarySyntax binary)
    {
        var left = Build(binary.Left);
        var right = Build(binary.Right);
        var leftValue = Expression.Variable(typeof(object), "left");
        var rightValue = Expression.Variable(typeof(object), "right");
        var leftPlan = new PlanValue(leftValue, left.ArgumentInfo);
        var rightPlan = new PlanValue(rightValue, right.ArgumentInfo);
        var rightResult = Expression.Block(
            [rightValue],
            Expression.Assign(rightValue, right.Expression),
            Expression.Condition(
                IsUnset(rightValue),
                Unset(),
                Box(BuildBooleanConversion(rightPlan))));
        var decidedValue = binary.Operator != BinaryOperator.LogicalAnd;
        var evaluateRightWhen = binary.Operator == BinaryOperator.LogicalAnd;

        return Expression.Block(
            [leftValue],
            Expression.Assign(leftValue, left.Expression),
            Expression.Condition(
                IsUnset(leftValue),
                Unset(),
                Expression.Condition(
                    Expression.Equal(
                        BuildBooleanConversion(leftPlan),
                        Expression.Constant(evaluateRightWhen)),
                    rightResult,
                    Expression.Constant(decidedValue, typeof(object)))));
    }

    private BlockExpression BuildConditionalValue(PlanValue condition, Expression whenTrue, Expression whenFalse)
    {
        var value = Expression.Variable(typeof(object), "condition");
        var conditionPlan = new PlanValue(value, condition.ArgumentInfo);
        return Expression.Block(
            [value],
            Expression.Assign(value, condition.Expression),
            Expression.Condition(
                IsUnset(value),
                Unset(),
                Expression.Condition(
                    BuildBooleanConversion(conditionPlan),
                    whenTrue,
                    whenFalse)));
    }

    private Expression BuildBooleanConversion(PlanValue value)
    {
        var key = DynamicBinderKey.Conversion(typeof(bool), value.ArgumentInfo);
        var binder = _registry.GetOrAddBinder(
            key,
            () => new ImplicitConversionDynamicBinder(typeof(bool), value.ArgumentInfo));

        // A declared site preserves the typed Boolean result and exposes its closed delegate shape to NativeAOT.
        // Expression.Dynamic otherwise discovers that shape through reflection while initializing the interpreted plan.
        var site = CallSite<BooleanConversionSiteTarget>.Create(binder);
        return Expression.Call(
            InvokeBooleanConversionMethod,
            Expression.Constant(site),
            value.Expression);
    }

    private static DynamicExpression BuildDynamic(ExpressionDynamicBinder binder, IEnumerable<Expression> arguments)
    {
        var dynamicArguments = new List<Expression>
        {
            Expression.Constant(DynamicOperationTarget.Instance)
        };
        dynamicArguments.AddRange(arguments);
        return Expression.Dynamic(binder, binder.ReturnType, dynamicArguments);
    }

    private static object? GetValue(IList<object?> values, int index) => values[index];

    private static bool InvokeBooleanConversion(CallSite<BooleanConversionSiteTarget> site, object? value) =>
        site.Target(site, DynamicOperationTarget.Instance, value);

    private static object ThrowNoSwitchMatch() =>
        throw new ExpressionBindingException("No switch arm matches the input value.");

    private static BinaryExpression IsUnset(Expression value) =>
        Expression.ReferenceEqual(value, Unset());

    private static ConstantExpression Unset() =>
        Expression.Constant(AvaloniaProperty.UnsetValue, typeof(UnsetValueType));

    private static Expression Box(Expression expression) =>
        expression.Type == typeof(object) ? expression : Expression.Convert(expression, typeof(object));

    private readonly record struct PlanValue(Expression Expression, DynamicArgumentInfo ArgumentInfo)
    {
        internal static PlanValue FromExpression(Expression expression) =>
            new(expression, DynamicArgumentInfo.Value);

        internal static PlanValue FromValue(object? value) =>
            new(Expression.Constant(value, typeof(object)), DynamicArgumentInfo.Value);
    }

    private delegate bool BooleanConversionSiteTarget(
        CallSite site,
        DynamicOperationTarget target,
        object? value);
}

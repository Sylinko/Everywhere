using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Avalonia;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia.Binding;

internal readonly record struct DynamicArgumentInfo(bool IsNumericLiteral, decimal NumericLiteral)
{
    internal static DynamicArgumentInfo Value { get; } = new(false, 0);

    internal static DynamicArgumentInfo FromNumericLiteral(decimal value) => new(true, value);

    internal void AppendCacheKey(StringBuilder builder)
    {
        if (!IsNumericLiteral)
        {
            builder.Append('v');
            return;
        }

        builder.Append('n');
        foreach (var part in decimal.GetBits(NumericLiteral))
        {
            builder.Append(':').Append(part);
        }
    }
}

// ReSharper disable once NotAccessedPositionalProperty.Global
internal readonly record struct DynamicBinderKey(string Value)
{
    internal static DynamicBinderKey Function(string name, IReadOnlyList<DynamicArgumentInfo> arguments) =>
        Create($"function:{name.ToUpperInvariant()}", arguments);

    internal static DynamicBinderKey Unary(UnaryOperator @operator, DynamicArgumentInfo operand) =>
        Create($"unary:{(int)@operator}", [operand]);

    internal static DynamicBinderKey Binary(BinaryOperator @operator, DynamicArgumentInfo left, DynamicArgumentInfo right) =>
        Create($"binary:{(int)@operator}", [left, right]);

    internal static DynamicBinderKey Conversion(Type targetType, DynamicArgumentInfo value) =>
        Create($"conversion:{targetType.AssemblyQualifiedName}", [value]);

    private static DynamicBinderKey Create(string prefix, IReadOnlyList<DynamicArgumentInfo> arguments)
    {
        var builder = new StringBuilder(prefix);
        foreach (var argument in arguments)
        {
            builder.Append('|');
            argument.AppendCacheKey(builder);
        }

        return new DynamicBinderKey(builder.ToString());
    }
}

internal abstract class ExpressionDynamicBinder(Type returnType) : DynamicMetaObjectBinder
{
    public sealed override Type ReturnType { get; } = returnType;

    private static readonly MethodInfo ReadRegistryVersionMethod = ((Func<ExpressionRegistry, int>)ReadRegistryVersion).Method;
    private static readonly MethodInfo CreateBindingExceptionMethod = ((Func<string, ExpressionBindingException>)CreateBindingException).Method;

    public sealed override DynamicMetaObject Bind(DynamicMetaObject target, DynamicMetaObject[] args)
    {
        if (!target.HasValue || args.Any(static argument => !argument.HasValue))
        {
            var deferred = new DynamicMetaObject[args.Length + 1];
            deferred[0] = target;
            Array.Copy(args, 0, deferred, 1, args.Length);
            return Defer(deferred);
        }

        var restrictions = BindingRestrictions.GetInstanceRestriction(target.Expression, target.Value);
        foreach (var argument in args)
        {
            restrictions = restrictions.Merge(GetValueRestriction(argument));
        }

        Expression body;
        int? registryVersion;
        try
        {
            (body, registryVersion) = BindCore(args);
        }
        catch (ExpressionBindingException exception)
        {
            body = CreateThrow(exception.Message, ReturnType);
            registryVersion = GetRegistryVersionForErrorRule();
        }

        if (registryVersion is { } version)
        {
            var registry = GetRegistry();
            restrictions = restrictions.Merge(
                BindingRestrictions.GetExpressionRestriction(
                    Expression.Equal(
                        Expression.Call(ReadRegistryVersionMethod, Expression.Constant(registry)),
                        Expression.Constant(version))));
        }

        if (body.Type != ReturnType)
        {
            body = Expression.Convert(body, ReturnType);
        }

        return new DynamicMetaObject(body, restrictions);
    }

    protected abstract (Expression Body, int? RegistryVersion) BindCore(DynamicMetaObject[] args);

    protected virtual ExpressionRegistry GetRegistry() =>
        throw new InvalidOperationException("This dynamic binder does not depend on a function registry.");

    protected virtual int? GetRegistryVersionForErrorRule() => null;

    private static BindingRestrictions GetValueRestriction(DynamicMetaObject value)
    {
        if (value.Value is null || ReferenceEquals(value.Value, AvaloniaProperty.UnsetValue))
        {
            return BindingRestrictions.GetInstanceRestriction(value.Expression, value.Value);
        }

        return BindingRestrictions.GetTypeRestriction(value.Expression, value.Value.GetType());
    }

    private static UnaryExpression CreateThrow(string message, Type returnType) =>
        Expression.Throw(
            Expression.Call(CreateBindingExceptionMethod, Expression.Constant(message)),
            returnType);

    private static int ReadRegistryVersion(ExpressionRegistry registry) => registry.Version;

    private static ExpressionBindingException CreateBindingException(string message) => new(message);
}

internal sealed class FunctionCallDynamicBinder(
    ExpressionRegistry registry,
    string name,
    DynamicArgumentInfo[] argumentInfo
) : ExpressionDynamicBinder(typeof(object))
{
    protected override (Expression Body, int? RegistryVersion) BindCore(DynamicMetaObject[] args)
    {
        var snapshot = registry.GetFunctionsSnapshot(name);
        var body = ExpressionBinder.BindFunction(name, snapshot.Functions, args, argumentInfo);
        return (body, snapshot.Version);
    }

    protected override ExpressionRegistry GetRegistry() => registry;

    protected override int? GetRegistryVersionForErrorRule() => registry.Version;
}

internal sealed class UnaryOperationDynamicBinder(
    UnaryOperator @operator,
    DynamicArgumentInfo operandInfo
) : ExpressionDynamicBinder(typeof(object))
{
    protected override (Expression Body, int? RegistryVersion) BindCore(DynamicMetaObject[] args) =>
        (ExpressionBinder.BindUnary(@operator, args[0], operandInfo), null);
}

internal sealed class BinaryOperationDynamicBinder(
    BinaryOperator @operator,
    DynamicArgumentInfo leftInfo,
    DynamicArgumentInfo rightInfo
) : ExpressionDynamicBinder(typeof(object))
{
    protected override (Expression Body, int? RegistryVersion) BindCore(DynamicMetaObject[] args) =>
        (ExpressionBinder.BindBinary(@operator, args[0], leftInfo, args[1], rightInfo), null);
}

internal sealed class ImplicitConversionDynamicBinder(
    Type targetType,
    DynamicArgumentInfo valueInfo
) : ExpressionDynamicBinder(targetType)
{
    protected override (Expression Body, int? RegistryVersion) BindCore(DynamicMetaObject[] args) =>
        (ExpressionBinder.BindImplicitConversion(args[0], valueInfo, ReturnType), null);
}

internal sealed class DynamicOperationTarget
{
    internal static DynamicOperationTarget Instance { get; } = new();

    private DynamicOperationTarget()
    {
    }
}

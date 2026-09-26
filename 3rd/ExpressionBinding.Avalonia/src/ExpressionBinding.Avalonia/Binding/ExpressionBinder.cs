using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia.Binding;

internal sealed class ExpressionBinder
{
    internal static Expression BindFunction(
        string name,
        IReadOnlyList<RegisteredFunction> functions,
        DynamicMetaObject[] arguments,
        DynamicArgumentInfo[] argumentInfo)
    {
        var boundArguments = CreateBoundValues(arguments, argumentInfo);
        return Box(Materialize(BindFunction(name, functions, boundArguments)));
    }

    internal static Expression BindUnary(
        UnaryOperator @operator,
        DynamicMetaObject operand,
        DynamicArgumentInfo operandInfo)
    {
        return Box(Materialize(BindUnary(@operator, CreateBoundValue(operand, operandInfo))));
    }

    internal static Expression BindBinary(
        BinaryOperator @operator,
        DynamicMetaObject left,
        DynamicArgumentInfo leftInfo,
        DynamicMetaObject right,
        DynamicArgumentInfo rightInfo)
    {
        return Box(
            Materialize(
                BindBinary(
                    @operator,
                    CreateBoundValue(left, leftInfo),
                    CreateBoundValue(right, rightInfo))));
    }

    internal static Expression BindImplicitConversion(
        DynamicMetaObject value,
        DynamicArgumentInfo valueInfo,
        Type targetType)
    {
        var boundValue = CreateBoundValue(value, valueInfo);
        if (TryConvert(boundValue, targetType, out var converted, out _))
        {
            return converted;
        }

        throw new ExpressionBindingException(
            $"No implicit conversion exists from '{Describe(boundValue)}' to '{targetType.Name}'.");
    }

    internal static Expression MaterializeNumericLiteral(decimal value) => Materialize(BoundValue.FromLiteral(value));

    private static BoundValue[] CreateBoundValues(DynamicMetaObject[] arguments, DynamicArgumentInfo[] argumentInfo)
    {
        if (arguments.Length != argumentInfo.Length)
        {
            throw new InvalidOperationException("Dynamic argument metadata does not match the call-site signature.");
        }

        var result = new BoundValue[arguments.Length];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = CreateBoundValue(arguments[i], argumentInfo[i]);
        }

        return result;
    }

    private static BoundValue CreateBoundValue(DynamicMetaObject value, DynamicArgumentInfo argumentInfo)
    {
        if (argumentInfo.IsNumericLiteral)
        {
            return BoundValue.FromLiteral(argumentInfo.NumericLiteral);
        }

        if (value.Value is null)
        {
            return BoundValue.Null;
        }

        if (ReferenceEquals(value.Value, AvaloniaProperty.UnsetValue))
        {
            return BoundValue.Unset;
        }

        return BoundValue.FromExpression(Expression.Convert(value.Expression, value.Value.GetType()));
    }

    private static Expression Box(Expression expression) =>
        expression.Type == typeof(object) ? expression : Expression.Convert(expression, typeof(object));

    private static BoundValue BindFunction(
        string name,
        IReadOnlyList<RegisteredFunction> functions,
        BoundValue[] arguments)
    {
        if (functions.Count == 0)
        {
            throw new ExpressionBindingException($"No function named '{name}' is registered.");
        }

        var candidates = new List<FunctionCandidate>();

        foreach (var registeredFunction in functions)
        {
            if (TryCreateFunctionCandidate(registeredFunction, arguments, expanded: false, out var normalCandidate))
            {
                candidates.Add(normalCandidate);
                continue;
            }

            if (registeredFunction.IsParameterArray &&
                TryCreateFunctionCandidate(registeredFunction, arguments, expanded: true, out var expandedCandidate))
            {
                candidates.Add(expandedCandidate);
            }
        }

        if (candidates.Count == 0)
        {
            if (arguments.Any(static argument => argument.IsUnset) &&
                functions.Any(function =>
                    CanPropagateUnset(function, arguments, expanded: false) ||
                    function.IsParameterArray && CanPropagateUnset(function, arguments, expanded: true)))
            {
                return BoundValue.Unset;
            }

            if (!RuntimeFeature.IsDynamicCodeSupported && functions.Any(static function => function.IsGenericDefinition))
            {
                throw new ExpressionBindingException(
                    $"No overload of '{name}' accepts ({Describe(arguments)}). Open generic functions are not " +
                    "available when dynamic code is disabled; register the required closed delegate instead.");
            }

            throw new ExpressionBindingException($"No overload of '{name}' accepts ({Describe(arguments)}).");
        }

        var bestCandidates = candidates
            .Where(candidate => !candidates.Any(other =>
                !ReferenceEquals(candidate, other) && IsBetterFunctionMember(other, candidate, arguments)))
            .ToArray();

        if (bestCandidates.Length != 1)
        {
            throw new ExpressionBindingException($"The call '{name}({Describe(arguments)})' is ambiguous.");
        }

        var best = bestCandidates[0];
        Expression invocation;
        if (best.Function.Function is { } targetFunction)
        {
            invocation = Expression.Invoke(Expression.Constant(targetFunction, targetFunction.GetType()), best.InvocationArguments);
        }
        else
        {
            invocation = Expression.Call(best.Method, best.InvocationArguments);
        }

        return BoundValue.FromExpression(invocation);
    }

    private static bool CanPropagateUnset(RegisteredFunction function, BoundValue[] arguments, bool expanded)
    {
        var declaredParameters = function.Parameters;
        if ((!expanded && declaredParameters.Length != arguments.Length) ||
            (expanded && (!function.IsParameterArray || arguments.Length < declaredParameters.Length - 1)))
        {
            return false;
        }

        var method = function.Method;
        if (function.IsGenericDefinition)
        {
            if (!RuntimeFeature.IsDynamicCodeSupported)
            {
                return false;
            }

            if (TryConstructGenericMethod(method, declaredParameters, arguments, expanded, out method))
            {
                declaredParameters = method.GetParameters();
            }
            else
            {
                return CanPotentiallyConstructGenericMethod(method, declaredParameters, arguments, expanded);
            }
        }

        var effectiveParameterTypes = GetEffectiveParameterTypes(declaredParameters, arguments.Length, expanded);
        for (var i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsUnset && !TryConvert(arguments[i], effectiveParameterTypes[i], out _, out _))
            {
                return false;
            }
        }

        return true;
    }

    private static bool CanPotentiallyConstructGenericMethod(
        MethodInfo method,
        ParameterInfo[] parameters,
        BoundValue[] arguments,
        bool expanded)
    {
        var parameterTypes = GetEffectiveParameterTypes(parameters, arguments.Length, expanded);
        var evidence = method.GetGenericArguments().ToDictionary(static parameter => parameter, static _ => new List<Type>());

        for (var i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsUnset)
            {
                continue;
            }

            var parameterType = parameterTypes[i];
            if (!parameterType.ContainsGenericParameters)
            {
                if (!TryConvert(arguments[i], parameterType, out _, out _))
                {
                    return false;
                }

                continue;
            }

            var argumentType = GetInferenceType(arguments[i]);
            if (argumentType is not null && !TryCollectTypeEvidence(parameterType, argumentType, evidence))
            {
                return false;
            }
        }

        var typeArguments = new Type[evidence.Count];
        foreach (var pair in evidence)
        {
            if (!TryResolveInferredType(pair.Value, out var inferredType))
            {
                return true;
            }

            typeArguments[pair.Key.GenericParameterPosition] = inferredType;
        }

        try
        {
            method.MakeGenericMethod(typeArguments);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryCreateFunctionCandidate(
        RegisteredFunction function,
        BoundValue[] arguments,
        bool expanded,
        out FunctionCandidate candidate)
    {
        candidate = null!;
        var declaredParameters = function.Parameters;
        if ((!expanded && declaredParameters.Length != arguments.Length) ||
            (expanded && (!function.IsParameterArray || arguments.Length < declaredParameters.Length - 1)))
        {
            return false;
        }

        var method = function.Method;
        if (function.IsGenericDefinition)
        {
            // Native AOT cannot guarantee code for a type argument discovered only from runtime binding values.
            // Closed delegate registrations remain available there and are used by all built-in functions.
            if (!RuntimeFeature.IsDynamicCodeSupported ||
                !TryConstructGenericMethod(method, declaredParameters, arguments, expanded, out method))
            {
                return false;
            }

            declaredParameters = method.GetParameters();
        }

        var effectiveParameterTypes = GetEffectiveParameterTypes(declaredParameters, arguments.Length, expanded);
        var converted = new Expression[arguments.Length];
        var conversions = new FunctionConversion[arguments.Length];
        for (var i = 0; i < arguments.Length; i++)
        {
            var targetType = effectiveParameterTypes[i];
            // ReSharper disable once RedundantSuppressNullableWarningExpression
            if (!TryConvert(arguments[i], targetType, out converted[i]!, out var score))
            {
                return false;
            }

            conversions[i] = new FunctionConversion(targetType, score);
        }

        Expression[] invocationArguments;
        if (!expanded)
        {
            invocationArguments = converted;
        }
        else
        {
            var fixedCount = declaredParameters.Length - 1;
            var elementType = declaredParameters[^1].ParameterType.GetElementType()!;
            invocationArguments = new Expression[declaredParameters.Length];
            Array.Copy(converted, invocationArguments, fixedCount);
            invocationArguments[^1] = Expression.NewArrayInit(elementType, converted.Skip(fixedCount));
        }

        candidate = new FunctionCandidate(
            function,
            method,
            invocationArguments,
            conversions,
            expanded,
            function.IsGenericDefinition);
        return true;
    }

    private static Type[] GetEffectiveParameterTypes(ParameterInfo[] parameters, int argumentCount, bool expanded)
    {
        if (!expanded)
        {
            return parameters.Select(static parameter => parameter.ParameterType).ToArray();
        }

        var fixedCount = parameters.Length - 1;
        var elementType = parameters[^1].ParameterType.GetElementType()!;
        var result = new Type[argumentCount];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = i < fixedCount ? parameters[i].ParameterType : elementType;
        }

        return result;
    }

    private static bool TryConstructGenericMethod(
        MethodInfo method,
        ParameterInfo[] parameters,
        BoundValue[] arguments,
        bool expanded,
        out MethodInfo constructedMethod)
    {
        constructedMethod = null!;
        var parameterTypes = GetEffectiveParameterTypes(parameters, arguments.Length, expanded);
        var evidence = method.GetGenericArguments().ToDictionary(static parameter => parameter, static _ => new List<Type>());

        for (var i = 0; i < arguments.Length; i++)
        {
            var argumentType = GetInferenceType(arguments[i]);
            if (argumentType is not null && !TryCollectTypeEvidence(parameterTypes[i], argumentType, evidence))
            {
                return false;
            }
        }

        var typeArguments = new Type[evidence.Count];
        foreach (var pair in evidence)
        {
            if (!TryResolveInferredType(pair.Value, out var inferredType))
            {
                return false;
            }

            typeArguments[pair.Key.GenericParameterPosition] = inferredType;
        }

        try
        {
            constructedMethod = method.MakeGenericMethod(typeArguments);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Type? GetInferenceType(BoundValue value)
    {
        if (value.IsNull || value.IsUnset)
        {
            return null;
        }

        return value.IsLiteral ? GetDefaultLiteralType(value.Literal) : value.ValueExpression!.Type;
    }

    private static bool TryCollectTypeEvidence(
        Type parameterType,
        Type argumentType,
        IReadOnlyDictionary<Type, List<Type>> evidence)
    {
        if (parameterType.IsGenericParameter)
        {
            if (evidence.TryGetValue(parameterType, out var bounds))
            {
                bounds.Add(argumentType);
            }

            return true;
        }

        if (parameterType.IsArray)
        {
            return !parameterType.ContainsGenericParameters ||
                argumentType.IsArray &&
                parameterType.GetArrayRank() == argumentType.GetArrayRank() &&
                TryCollectTypeEvidence(parameterType.GetElementType()!, argumentType.GetElementType()!, evidence);
        }

        if (!parameterType.IsGenericType || !parameterType.ContainsGenericParameters)
        {
            return true;
        }

        var matchingType = FindConstructedType(argumentType, parameterType.GetGenericTypeDefinition());
        if (matchingType is null)
        {
            return false;
        }

        var parameterArguments = parameterType.GetGenericArguments();
        var actualArguments = matchingType.GetGenericArguments();
        for (var i = 0; i < parameterArguments.Length; i++)
        {
            if (!TryCollectTypeEvidence(parameterArguments[i], actualArguments[i], evidence))
            {
                return false;
            }
        }

        return true;
    }

    private static Type? FindConstructedType(Type type, Type genericTypeDefinition)
    {
        var matches = new List<Type>();
        if (type.IsGenericType && type.GetGenericTypeDefinition() == genericTypeDefinition)
        {
            matches.Add(type);
        }

        foreach (var implementedInterface in type.GetInterfaces())
        {
            if (implementedInterface.IsGenericType &&
                implementedInterface.GetGenericTypeDefinition() == genericTypeDefinition)
            {
                matches.Add(implementedInterface);
            }
        }

        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
        {
            if (baseType.IsGenericType && baseType.GetGenericTypeDefinition() == genericTypeDefinition)
            {
                matches.Add(baseType);
            }
        }

        var distinctMatches = matches.Distinct().ToArray();
        return distinctMatches.Length == 1 ? distinctMatches[0] : null;
    }

    private static bool TryResolveInferredType(IReadOnlyList<Type> evidence, out Type inferredType)
    {
        inferredType = null!;
        if (evidence.Count == 0)
        {
            return false;
        }

        var candidates = evidence.Distinct().ToList();
        Type? numericType = null;
        if (candidates.All(IsNumeric))
        {
            numericType = candidates[0];
            foreach (var next in candidates.Skip(1))
            {
                numericType = numericType is null ? null : GetBinaryNumericType(numericType, next);
            }
        }

        if (numericType is not null && !candidates.Contains(numericType))
        {
            candidates.Add(numericType);
        }

        var applicable = candidates
            .Where(candidate => evidence.All(source => CanImplicitlyConvertType(source, candidate)))
            .ToArray();
        if (applicable.Length == 0)
        {
            return false;
        }

        var best = applicable
            .Where(candidate => !applicable.Any(other =>
                other != candidate &&
                CanImplicitlyConvertType(other, candidate) &&
                !CanImplicitlyConvertType(candidate, other)))
            .ToArray();
        if (best.Length != 1)
        {
            return false;
        }

        inferredType = best[0];
        return true;
    }

    private static bool IsBetterFunctionMember(
        FunctionCandidate left,
        FunctionCandidate right,
        BoundValue[] arguments)
    {
        var hasBetterConversion = false;
        for (var i = 0; i < arguments.Length; i++)
        {
            var comparison = CompareFunctionConversions(left.Conversions[i], right.Conversions[i]);
            if (comparison is ConversionComparison.RightBetter or ConversionComparison.Incomparable)
            {
                return false;
            }

            hasBetterConversion |= comparison == ConversionComparison.LeftBetter;
        }

        if (hasBetterConversion)
        {
            return true;
        }

        if (left.IsExpanded != right.IsExpanded)
        {
            return !left.IsExpanded;
        }

        if (left.IsGeneric != right.IsGeneric &&
            left.Conversions.Select(static conversion => conversion.TargetType)
                .SequenceEqual(right.Conversions.Select(static conversion => conversion.TargetType)))
        {
            return !left.IsGeneric;
        }

        return false;
    }

    private static ConversionComparison CompareFunctionConversions(FunctionConversion left, FunctionConversion right)
    {
        if (left.TargetType == right.TargetType)
        {
            return ConversionComparison.Equal;
        }

        if (left.Score != right.Score)
        {
            return left.Score < right.Score ? ConversionComparison.LeftBetter : ConversionComparison.RightBetter;
        }

        var leftToRight = CanImplicitlyConvertType(left.TargetType, right.TargetType);
        var rightToLeft = CanImplicitlyConvertType(right.TargetType, left.TargetType);
        return (leftToRight, rightToLeft) switch
        {
            (true, false) => ConversionComparison.LeftBetter,
            (false, true) => ConversionComparison.RightBetter,
            _ => ConversionComparison.Incomparable
        };
    }

    private static bool CanImplicitlyConvertType(Type source, Type target) =>
        source == target ||
        target.IsAssignableFrom(source) ||
        IsImplicitNumericConversion(source, target) ||
        FindImplicitConversion(source, target) is not null;

    private static BoundValue BindUnary(UnaryOperator @operator, BoundValue operand)
    {
        if (operand.IsUnset)
        {
            return BoundValue.Unset;
        }

        if (operand.IsLiteral)
        {
            return @operator switch
            {
                UnaryOperator.Plus => operand,
                UnaryOperator.Negate => BoundValue.FromLiteral(-operand.Literal),
                _ => BindMaterializedUnary(@operator, BoundValue.FromExpression(Materialize(operand)))
            };
        }

        if (operand.IsNull)
        {
            throw new ExpressionBindingException($"Unary operator '{GetOperatorText(@operator)}' cannot be applied to null.");
        }

        return BindMaterializedUnary(@operator, operand);
    }

    private static BoundValue BindMaterializedUnary(UnaryOperator @operator, BoundValue operand)
    {
        var expression = operand.ValueExpression!;
        if (IsSmallIntegral(expression.Type))
        {
            expression = Expression.Convert(expression, typeof(int));
        }

        try
        {
            var nodeType = @operator switch
            {
                UnaryOperator.Plus => ExpressionType.UnaryPlus,
                UnaryOperator.Negate => ExpressionType.Negate,
                UnaryOperator.LogicalNot => ExpressionType.Not,
                UnaryOperator.OnesComplement => ExpressionType.OnesComplement,
                _ => throw new ExpressionBindingException($"Unsupported unary operator '{@operator}'.")
            };
            return BoundValue.FromExpression(Expression.MakeUnary(nodeType, expression, type: null!, method: null!));
        }
        catch (InvalidOperationException exception)
        {
            throw new ExpressionBindingException(
                $"Operator '{GetOperatorText(@operator)}' is not defined for '{expression.Type.Name}'.",
                exception);
        }
    }

    private static BoundValue BindBinary(BinaryOperator @operator, BoundValue left, BoundValue right)
    {
        if (@operator is BinaryOperator.Equal or BinaryOperator.NotEqual && (left.IsUnset || right.IsUnset))
        {
            var equal = left.IsUnset && right.IsUnset;
            return BoundValue.FromExpression(
                Expression.Constant(
                    @operator == BinaryOperator.Equal ? equal : !equal));
        }

        if (left.IsUnset || right.IsUnset)
        {
            return BoundValue.Unset;
        }

        if (TryBindShift(@operator, left, right, out var shift))
        {
            return shift;
        }

        if (TryBindBooleanBinary(@operator, left, right, out var boolean))
        {
            return boolean;
        }

        if (TryBindStringConcatenation(@operator, left, right, out var concatenation))
        {
            return concatenation;
        }

        if (TryBindNumericBinary(@operator, left, right, out var numeric))
        {
            return numeric;
        }

        if (TryBindUserOperator(@operator, left, right, out var custom))
        {
            return custom;
        }

        if (TryBindNullEquality(@operator, left, right, out var nullEquality))
        {
            return nullEquality;
        }

        if (TryBindSameTypeEquality(@operator, left, right, out var sameTypeEquality))
        {
            return sameTypeEquality;
        }

        throw new ExpressionBindingException(
            $"Operator '{GetOperatorText(@operator)}' is not defined for '{Describe(left)}' and '{Describe(right)}'.");
    }

    private static bool TryBindShift(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        if (@operator is not (BinaryOperator.LeftShift or BinaryOperator.RightShift))
        {
            return false;
        }

        var leftType = left.IsLiteral ? GetDefaultLiteralType(left.Literal) : left.ValueExpression?.Type;
        if (leftType is null || !IsIntegral(leftType))
        {
            return false;
        }

        leftType = PromoteSmallIntegral(leftType);
        if (!TryConvert(left, leftType, out var convertedLeft, out _) ||
            !TryConvert(right, typeof(int), out var convertedRight, out _))
        {
            return false;
        }

        result = BoundValue.FromExpression(
            Expression.MakeBinary(
                GetExpressionType(@operator),
                convertedLeft,
                convertedRight));
        return true;
    }

    private static bool TryBindBooleanBinary(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        if (@operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual or
                BinaryOperator.BitwiseAnd or BinaryOperator.ExclusiveOr or BinaryOperator.BitwiseOr) ||
            left.IsLiteral || right.IsLiteral || left.IsNull || right.IsNull ||
            left.ValueExpression!.Type != typeof(bool) || right.ValueExpression!.Type != typeof(bool))
        {
            return false;
        }

        result = BoundValue.FromExpression(
            Expression.MakeBinary(
                GetExpressionType(@operator),
                left.ValueExpression,
                right.ValueExpression));
        return true;
    }

    private static bool TryBindStringConcatenation(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        if (@operator != BinaryOperator.Add ||
            GetRuntimeType(left) != typeof(string) && GetRuntimeType(right) != typeof(string))
        {
            return false;
        }

        var concat = typeof(string).GetMethod(nameof(string.Concat), [typeof(object), typeof(object)]) ??
            throw new InvalidOperationException("The string concatenation method is unavailable.");
        result = BoundValue.FromExpression(
            Expression.Call(
                concat,
                Expression.Convert(Materialize(left), typeof(object)),
                Expression.Convert(Materialize(right), typeof(object))));
        return true;
    }

    private static Type? GetRuntimeType(BoundValue value)
    {
        if (value.IsNull || value.IsUnset)
        {
            return null;
        }

        return value.IsLiteral ? GetDefaultLiteralType(value.Literal) : value.ValueExpression!.Type;
    }

    private static bool TryBindNullEquality(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        if (@operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual) ||
            (!left.IsNull && !right.IsNull))
        {
            return false;
        }

        if (left.IsNull && right.IsNull)
        {
            result = BoundValue.FromExpression(Expression.Constant(@operator == BinaryOperator.Equal));
            return true;
        }

        var value = left.IsNull ? right : left;
        if (value.IsLiteral || value.ValueExpression!.Type.IsValueType)
        {
            result = BoundValue.FromExpression(Expression.Constant(@operator == BinaryOperator.NotEqual));
            return true;
        }

        var nullValue = Expression.Constant(null, value.ValueExpression.Type);
        result = BoundValue.FromExpression(
            Expression.MakeBinary(
                GetExpressionType(@operator),
                left.IsNull ? nullValue : value.ValueExpression,
                right.IsNull ? nullValue : value.ValueExpression));
        return true;
    }

    private static bool TryBindSameTypeEquality(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        if (@operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual) ||
            left.IsLiteral || right.IsLiteral || left.IsNull || right.IsNull ||
            left.ValueExpression!.Type != right.ValueExpression!.Type)
        {
            return false;
        }

        try
        {
            result = BoundValue.FromExpression(
                Expression.MakeBinary(
                    GetExpressionType(@operator),
                    left.ValueExpression,
                    right.ValueExpression));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryBindNumericBinary(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;

        if (@operator is BinaryOperator.LeftShift or BinaryOperator.RightShift or
            BinaryOperator.LogicalAnd or BinaryOperator.LogicalOr)
        {
            return false;
        }

        if ((!left.IsLiteral && (left.IsNull || !IsNumeric(left.ValueExpression!.Type))) ||
            (!right.IsLiteral && (right.IsNull || !IsNumeric(right.ValueExpression!.Type))))
        {
            return false;
        }

        var leftType = left.IsLiteral ? GetDefaultLiteralType(left.Literal) : left.ValueExpression!.Type;
        var rightType = right.IsLiteral ? GetDefaultLiteralType(right.Literal) : right.ValueExpression!.Type;

        Type? commonType;
        if (left.IsLiteral && !right.IsLiteral && rightType == typeof(decimal) ||
            right.IsLiteral && !left.IsLiteral && leftType == typeof(decimal))
        {
            commonType = typeof(decimal);
        }
        else
        {
            commonType = GetBinaryNumericType(leftType, rightType);
        }

        if (commonType is null ||
            ((@operator is BinaryOperator.BitwiseAnd or BinaryOperator.ExclusiveOr or BinaryOperator.BitwiseOr) &&
                !IsIntegral(commonType)) ||
            !TryConvert(left, commonType, out var convertedLeft, out _) ||
            !TryConvert(right, commonType, out var convertedRight, out _))
        {
            return false;
        }

        try
        {
            result = BoundValue.FromExpression(
                Expression.MakeBinary(
                    GetExpressionType(@operator),
                    convertedLeft,
                    convertedRight));
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool TryBindUserOperator(
        BinaryOperator @operator,
        BoundValue left,
        BoundValue right,
        out BoundValue result)
    {
        result = default;
        var methodName = GetOperatorMethodName(@operator);
        var methods = new List<MethodInfo>();

        AddOperatorMethods(left, methodName, methods);
        AddOperatorMethods(right, methodName, methods);

        var candidates = new List<OperatorCandidate>();

        foreach (var method in methods.Distinct())
        {
            var parameters = method.GetParameters();
            if (parameters.Length != 2 ||
                !TryConvert(left, parameters[0].ParameterType, out var convertedLeft, out var leftScore) ||
                !TryConvert(right, parameters[1].ParameterType, out var convertedRight, out var rightScore))
            {
                continue;
            }

            candidates.Add(
                new OperatorCandidate(
                    method,
                    convertedLeft,
                    convertedRight,
                    new FunctionConversion(parameters[0].ParameterType, leftScore),
                    new FunctionConversion(parameters[1].ParameterType, rightScore)));
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var bestCandidates = candidates
            .Where(candidate => !candidates.Any(other => !ReferenceEquals(candidate, other) && IsBetterOperator(other, candidate)))
            .ToArray();
        if (bestCandidates.Length != 1)
        {
            throw new ExpressionBindingException(
                $"Operator '{GetOperatorText(@operator)}' is ambiguous for '{Describe(left)}' and '{Describe(right)}'.");
        }

        var best = bestCandidates[0];
        result = BoundValue.FromExpression(
            Expression.MakeBinary(
                GetExpressionType(@operator),
                best.Left,
                best.Right,
                liftToNull: false,
                best.Method));
        return true;
    }

    private static bool IsBetterOperator(OperatorCandidate left, OperatorCandidate right)
    {
        var leftComparison = CompareFunctionConversions(left.LeftConversion, right.LeftConversion);
        var rightComparison = CompareFunctionConversions(left.RightConversion, right.RightConversion);
        if (leftComparison is ConversionComparison.RightBetter or ConversionComparison.Incomparable ||
            rightComparison is ConversionComparison.RightBetter or ConversionComparison.Incomparable)
        {
            return false;
        }

        return leftComparison == ConversionComparison.LeftBetter || rightComparison == ConversionComparison.LeftBetter;
    }

    private static Expression Materialize(BoundValue value)
    {
        if (value.IsLiteral)
        {
            var type = GetDefaultLiteralType(value.Literal);
            if (TryConvert(value, type, out var expression, out _))
            {
                return expression;
            }
        }

        if (value.IsNull)
        {
            return Expression.Constant(null, typeof(object));
        }

        if (value.IsUnset)
        {
            return Expression.Constant(AvaloniaProperty.UnsetValue, typeof(object));
        }

        return value.ValueExpression!;
    }

    private static bool TryConvert(
        BoundValue value,
        Type targetType,
        [NotNullWhen(true)] out Expression? expression,
        out int score)
    {
        if (value.IsLiteral)
        {
            return TryConvertLiteral(value.Literal, targetType, out expression, out score);
        }

        if (value.IsNull)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
            {
                expression = Expression.Constant(null, targetType);
                score = 1;
                return true;
            }

            expression = null!;
            score = 0;
            return false;
        }

        if (value.IsUnset)
        {
            if (targetType.IsInstanceOfType(AvaloniaProperty.UnsetValue))
            {
                expression = Expression.Convert(
                    Expression.Constant(AvaloniaProperty.UnsetValue, typeof(object)),
                    targetType);
                score = targetType == typeof(object) ? 0 : 1;
                return true;
            }

            expression = null;
            score = 0;
            return false;
        }

        var source = value.ValueExpression!;
        if (source.Type == targetType)
        {
            expression = source;
            score = 0;
            return true;
        }

        if (targetType.IsAssignableFrom(source.Type))
        {
            expression = Expression.Convert(source, targetType);
            score = targetType == typeof(object) ? 3 : 1;
            return true;
        }

        var nullableTarget = Nullable.GetUnderlyingType(targetType);
        if (nullableTarget is not null && TryConvert(value, nullableTarget, out var underlying, out score))
        {
            expression = Expression.Convert(underlying, targetType);
            score++;
            return true;
        }

        if (IsImplicitNumericConversion(source.Type, targetType))
        {
            expression = Expression.Convert(source, targetType);
            score = 1;
            return true;
        }

        var conversion = FindImplicitConversion(source.Type, targetType);
        if (conversion is not null)
        {
            expression = Expression.Convert(source, targetType, conversion);
            score = 2;
            return true;
        }

        expression = null!;
        score = 0;
        return false;
    }

    private static bool TryConvertLiteral(
        decimal value,
        Type targetType,
        [NotNullWhen(true)] out Expression? expression,
        out int score)
    {
        var nullableTarget = Nullable.GetUnderlyingType(targetType);
        if (nullableTarget is not null && TryConvertLiteral(value, nullableTarget, out var underlying, out score))
        {
            expression = Expression.Convert(underlying, targetType);
            score++;
            return true;
        }

        object? converted = null;
        var isInteger = decimal.Truncate(value) == value;

        if (targetType == typeof(decimal)) converted = value;
        else if (targetType == typeof(double)) converted = (double)value;
        else if (targetType == typeof(float)) converted = (float)value;
        else if (targetType == typeof(long) && isInteger && value is >= long.MinValue and <= long.MaxValue) converted = (long)value;
        else if (targetType == typeof(ulong) && isInteger && value is >= ulong.MinValue and <= ulong.MaxValue) converted = (ulong)value;
        else if (targetType == typeof(int) && isInteger && value is >= int.MinValue and <= int.MaxValue) converted = (int)value;
        else if (targetType == typeof(uint) && isInteger && value is >= uint.MinValue and <= uint.MaxValue) converted = (uint)value;
        else if (targetType == typeof(short) && isInteger && value is >= short.MinValue and <= short.MaxValue) converted = (short)value;
        else if (targetType == typeof(ushort) && isInteger && value is >= ushort.MinValue and <= ushort.MaxValue) converted = (ushort)value;
        else if (targetType == typeof(byte) && isInteger && value is >= byte.MinValue and <= byte.MaxValue) converted = (byte)value;
        else if (targetType == typeof(sbyte) && isInteger && value is >= sbyte.MinValue and <= sbyte.MaxValue) converted = (sbyte)value;

        if (converted is null)
        {
            var literalType = GetDefaultLiteralType(value);
            if (targetType.IsAssignableFrom(literalType) &&
                TryConvertLiteral(value, literalType, out var literal, out _))
            {
                expression = Expression.Convert(literal, targetType);
                score = targetType == typeof(object) ? 3 : 2;
                return true;
            }

            expression = null;
            score = 0;
            return false;
        }

        expression = Expression.Constant(converted, targetType);
        var defaultType = GetDefaultLiteralType(value);
        score = targetType == defaultType ? 0 : IsImplicitNumericConversion(defaultType, targetType) ? 1 : 2;
        return true;
    }

    private static Type GetDefaultLiteralType(decimal value)
    {
        if (decimal.Truncate(value) == value)
        {
            if (value is >= int.MinValue and <= int.MaxValue)
            {
                return typeof(int);
            }

            if (value is >= long.MinValue and <= long.MaxValue)
            {
                return typeof(long);
            }
        }

        return typeof(double);
    }

    private static Type? GetBinaryNumericType(Type left, Type right)
    {
        left = PromoteSmallIntegral(left);
        right = PromoteSmallIntegral(right);

        if (left == typeof(decimal) || right == typeof(decimal))
        {
            return left == typeof(float) || left == typeof(double) ||
                right == typeof(float) || right == typeof(double) ? null : typeof(decimal);
        }

        if (left == typeof(double) || right == typeof(double)) return typeof(double);
        if (left == typeof(float) || right == typeof(float)) return typeof(float);

        if (left == typeof(ulong) || right == typeof(ulong))
        {
            var other = left == typeof(ulong) ? right : left;
            return other == typeof(uint) || other == typeof(ulong) ? typeof(ulong) : null;
        }

        if (left == typeof(long) || right == typeof(long)) return typeof(long);
        if (left == typeof(uint) || right == typeof(uint))
        {
            return left == typeof(int) || right == typeof(int) ? typeof(long) : typeof(uint);
        }

        return typeof(int);
    }

    private static bool IsImplicitNumericConversion(Type source, Type target)
    {
        if (source.IsEnum || target.IsEnum)
        {
            return false;
        }

        if (source == target)
        {
            return true;
        }

        return Type.GetTypeCode(source) switch
        {
            TypeCode.SByte => target == typeof(short) || target == typeof(int) || target == typeof(long) ||
                target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.Byte => target == typeof(short) || target == typeof(ushort) || target == typeof(int) ||
                target == typeof(uint) || target == typeof(long) || target == typeof(ulong) ||
                target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.Int16 => target == typeof(int) || target == typeof(long) || target == typeof(float) ||
                target == typeof(double) || target == typeof(decimal),
            TypeCode.UInt16 => target == typeof(int) || target == typeof(uint) || target == typeof(long) ||
                target == typeof(ulong) || target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.Int32 => target == typeof(long) || target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.UInt32 => target == typeof(long) || target == typeof(ulong) || target == typeof(float) ||
                target == typeof(double) || target == typeof(decimal),
            TypeCode.Int64 => target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.UInt64 => target == typeof(float) || target == typeof(double) || target == typeof(decimal),
            TypeCode.Char => target == typeof(ushort) || target == typeof(int) || target == typeof(uint) ||
                target == typeof(long) || target == typeof(ulong) || target == typeof(float) ||
                target == typeof(double) || target == typeof(decimal),
            TypeCode.Single => target == typeof(double),
            _ => false
        };
    }

    private static MethodInfo? FindImplicitConversion(Type source, Type target) =>
        source.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Concat(target.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .FirstOrDefault(method =>
            {
                if (method.Name != "op_Implicit" || method.ReturnType != target)
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 1 && parameters[0].ParameterType == source;
            });

    private static void AddOperatorMethods(BoundValue value, string methodName, List<MethodInfo> methods)
    {
        if (value.IsLiteral || value.IsNull || value.IsUnset)
        {
            return;
        }

        methods.AddRange(
            value.ValueExpression!.Type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(method => method.Name == methodName));
    }

    private static bool IsNumeric(Type type) => !type.IsEnum && Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or
        TypeCode.Char or TypeCode.Single or TypeCode.Double or TypeCode.Decimal;

    private static bool IsIntegral(Type type) => !type.IsEnum && Type.GetTypeCode(type) is
        TypeCode.SByte or TypeCode.Byte or TypeCode.Int16 or TypeCode.UInt16 or
        TypeCode.Int32 or TypeCode.UInt32 or TypeCode.Int64 or TypeCode.UInt64 or TypeCode.Char;

    private static bool IsSmallIntegral(Type type) => type == typeof(sbyte) || type == typeof(byte) ||
        type == typeof(short) || type == typeof(ushort) || type == typeof(char);

    private static Type PromoteSmallIntegral(Type type) => IsSmallIntegral(type) ? typeof(int) : type;

    private static ExpressionType GetExpressionType(BinaryOperator @operator) => @operator switch
    {
        BinaryOperator.Add => ExpressionType.Add,
        BinaryOperator.Subtract => ExpressionType.Subtract,
        BinaryOperator.Multiply => ExpressionType.Multiply,
        BinaryOperator.Divide => ExpressionType.Divide,
        BinaryOperator.Modulo => ExpressionType.Modulo,
        BinaryOperator.LeftShift => ExpressionType.LeftShift,
        BinaryOperator.RightShift => ExpressionType.RightShift,
        BinaryOperator.LessThan => ExpressionType.LessThan,
        BinaryOperator.LessThanOrEqual => ExpressionType.LessThanOrEqual,
        BinaryOperator.GreaterThan => ExpressionType.GreaterThan,
        BinaryOperator.GreaterThanOrEqual => ExpressionType.GreaterThanOrEqual,
        BinaryOperator.Equal => ExpressionType.Equal,
        BinaryOperator.NotEqual => ExpressionType.NotEqual,
        BinaryOperator.BitwiseAnd => ExpressionType.And,
        BinaryOperator.ExclusiveOr => ExpressionType.ExclusiveOr,
        BinaryOperator.BitwiseOr => ExpressionType.Or,
        BinaryOperator.LogicalAnd => ExpressionType.AndAlso,
        BinaryOperator.LogicalOr => ExpressionType.OrElse,
        _ => throw new ExpressionBindingException($"Unsupported binary operator '{@operator}'.")
    };

    private static string GetOperatorMethodName(BinaryOperator @operator) => @operator switch
    {
        BinaryOperator.Add => "op_Addition",
        BinaryOperator.Subtract => "op_Subtraction",
        BinaryOperator.Multiply => "op_Multiply",
        BinaryOperator.Divide => "op_Division",
        BinaryOperator.Modulo => "op_Modulus",
        BinaryOperator.LeftShift => "op_LeftShift",
        BinaryOperator.RightShift => "op_RightShift",
        BinaryOperator.LessThan => "op_LessThan",
        BinaryOperator.LessThanOrEqual => "op_LessThanOrEqual",
        BinaryOperator.GreaterThan => "op_GreaterThan",
        BinaryOperator.GreaterThanOrEqual => "op_GreaterThanOrEqual",
        BinaryOperator.Equal => "op_Equality",
        BinaryOperator.NotEqual => "op_Inequality",
        BinaryOperator.BitwiseAnd => "op_BitwiseAnd",
        BinaryOperator.ExclusiveOr => "op_ExclusiveOr",
        BinaryOperator.BitwiseOr => "op_BitwiseOr",
        _ => throw new ExpressionBindingException($"Unsupported binary operator '{@operator}'.")
    };

    private static string GetOperatorText(BinaryOperator @operator) => @operator switch
    {
        BinaryOperator.Add => "+",
        BinaryOperator.Subtract => "-",
        BinaryOperator.Multiply => "*",
        BinaryOperator.Divide => "/",
        BinaryOperator.Modulo => "%",
        BinaryOperator.LeftShift => "<<",
        BinaryOperator.RightShift => ">>",
        BinaryOperator.LessThan => "<",
        BinaryOperator.LessThanOrEqual => "<=",
        BinaryOperator.GreaterThan => ">",
        BinaryOperator.GreaterThanOrEqual => ">=",
        BinaryOperator.Equal => "==",
        BinaryOperator.NotEqual => "!=",
        BinaryOperator.BitwiseAnd => "&",
        BinaryOperator.ExclusiveOr => "^",
        BinaryOperator.BitwiseOr => "|",
        BinaryOperator.LogicalAnd => "&&",
        BinaryOperator.LogicalOr => "||",
        _ => @operator.ToString()
    };

    private static string GetOperatorText(UnaryOperator @operator) => @operator switch
    {
        UnaryOperator.Plus => "+",
        UnaryOperator.Negate => "-",
        UnaryOperator.LogicalNot => "!",
        UnaryOperator.OnesComplement => "~",
        _ => @operator.ToString()
    };

    private static string Describe(IEnumerable<BoundValue> values) => string.Join(", ", values.Select(Describe));

    private static string Describe(BoundValue value) => value switch
    {
        { IsLiteral: true } => GetDefaultLiteralType(value.Literal).Name,
        { IsNull: true } => "null",
        { IsUnset: true } => "unset",
        _ => value.ValueExpression!.Type.Name
    };

    private readonly record struct BoundValue(
        Expression? ValueExpression,
        decimal Literal,
        bool IsLiteral,
        bool IsNull,
        bool IsUnset
    )
    {
        public static BoundValue Null => new(null, 0, false, true, false);

        public static BoundValue Unset => new(null, 0, false, false, true);

        public static BoundValue FromExpression(Expression expression) => new(expression, 0, false, false, false);

        public static BoundValue FromLiteral(decimal value) => new(null, value, true, false, false);
    }

    private sealed record FunctionCandidate(
        RegisteredFunction Function,
        MethodInfo Method,
        Expression[] InvocationArguments,
        FunctionConversion[] Conversions,
        bool IsExpanded,
        bool IsGeneric
    );

    private readonly record struct FunctionConversion(Type TargetType, int Score);

    private enum ConversionComparison
    {
        Equal,
        LeftBetter,
        RightBetter,
        Incomparable
    }

    private sealed record OperatorCandidate(
        MethodInfo Method,
        Expression Left,
        Expression Right,
        FunctionConversion LeftConversion,
        FunctionConversion RightConversion
    );
}

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ExpressionBinding.Avalonia.Binding;

namespace ExpressionBinding.Avalonia;

/// <summary>
/// Owns the function overloads available to an expression binding.
/// </summary>
public sealed class ExpressionRegistry
{
    /// <summary>
    /// Gets the registry used by expression bindings unless another registry is supplied.
    /// </summary>
    public static ExpressionRegistry Default { get; } = new();

    internal int Version => Volatile.Read(ref _version);

    private readonly Dictionary<string, List<RegisteredFunction>> _functions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<DynamicBinderKey, ExpressionDynamicBinder> _binders = [];
#if NET10_0_OR_GREATER
    private readonly Lock _sync = new();
#else
    private readonly object _sync = new();
#endif
    private int _version;

    /// <summary>
    /// Initializes a registry with the standard numeric and Avalonia geometry functions.
    /// </summary>
    public ExpressionRegistry()
    {
        RegisterBuiltIns();
    }

    internal FunctionRegistrySnapshot GetFunctionsSnapshot(string name)
    {
        lock (_sync)
        {
            return new FunctionRegistrySnapshot(
                _version,
                _functions.TryGetValue(name, out var functions) ? [.. functions] : []);
        }
    }

    internal ExpressionDynamicBinder GetOrAddBinder(DynamicBinderKey key, Func<ExpressionDynamicBinder> factory)
    {
        lock (_sync)
        {
            if (_binders.TryGetValue(key, out var binder))
            {
                return binder;
            }

            binder = factory();
            _binders.Add(key, binder);
            return binder;
        }
    }

    /// <summary>
    /// Registers a function overload under the specified expression name.
    /// </summary>
    /// <param name="name">The name of the expression.</param>
    /// <param name="function">The function to register.</param>
    public void RegisterFunction(string name, Delegate function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(function);

        var invoke = function.GetType().GetMethod(nameof(Action.Invoke), BindingFlags.Instance | BindingFlags.Public) ??
            throw new ArgumentException("The value is not an invocable delegate.", nameof(function));
        var parameterSource = HasCompatibleParameterList(invoke, function.Method) ? function.Method : invoke;
        AddFunctions(name, [RegisteredFunction.FromDelegate(function, invoke, parameterSource)]);
    }

    /// <summary>
    /// Registers all supported public static methods declared by <paramref name="declaringType"/>
    /// whose name is <paramref name="methodName"/>.
    /// </summary>
    /// <param name="name">The name of the expression.</param>
    /// <param name="declaringType">The type that declares the methods.</param>
    /// <param name="methodName">The name of the methods to register.</param>
    public void RegisterFunction(
        string name,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods)] Type declaringType,
        string methodName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(declaringType);
        ArgumentException.ThrowIfNullOrWhiteSpace(methodName);

        var methods = declaringType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == methodName)
            .ToArray();

        if (methods.Length == 0)
        {
            throw new ArgumentException(
                $"Type '{declaringType.FullName}' declares no public static method named '{methodName}'.",
                nameof(methodName));
        }

        AddFunctions(name, [.. methods.Select(RegisteredFunction.FromStaticMethod)]);
    }

    /// <summary>
    /// Registers a public static method as an expression function.
    /// This allows for registering methods that is not publicly accessible, such as private static methods, or methods from a type that is not publicly accessible.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="method"></param>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="ArgumentNullException"></exception>
    public void RegisterFunction(string name, MethodInfo method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(method);

        if (!method.IsStatic)
        {
            throw new ArgumentException(
                $"Method '{method.DeclaringType?.FullName}.{method.Name}' is not a static method.",
                nameof(method));
        }

        AddFunctions(name, [RegisteredFunction.FromStaticMethod(method)]);
    }

    private void AddFunctions(string name, RegisteredFunction[] newFunctions)
    {
        foreach (var function in newFunctions)
        {
            ValidateFunction(function);
        }

        for (var i = 0; i < newFunctions.Length; i++)
        {
            if (newFunctions.Skip(i + 1).Any(newFunctions[i].HasSameSignature))
            {
                throw new ArgumentException(
                    $"The registration contains more than one function with signature '{newFunctions[i].GetSignature()}'.",
                    nameof(newFunctions));
            }
        }

        lock (_sync)
        {
            if (!_functions.TryGetValue(name, out var functions))
            {
                functions = [];
                _functions.Add(name, functions);
            }

            foreach (var function in newFunctions)
            {
                if (functions.Any(existing => existing.HasSameSignature(function)))
                {
                    throw new ArgumentException(
                        $"A function named '{name}' with signature '{function.GetSignature()}' is already registered.",
                        nameof(newFunctions));
                }
            }

            functions.AddRange(newFunctions);
            _version++;
        }
    }

    private static void ValidateFunction(RegisteredFunction function)
    {
        if (function.Method.DeclaringType?.ContainsGenericParameters == true)
        {
            throw new ArgumentException("Expression functions cannot be declared on an open generic type.", nameof(function));
        }

        if (function.ReturnType == typeof(void) || function.ReturnType.IsByRef || function.ReturnType.IsByRefLike)
        {
            throw new ArgumentException("Expression functions must return a non-ref value.", nameof(function));
        }

        if (function.Parameters.Any(static parameter =>
                parameter.ParameterType.IsByRef ||
                parameter.ParameterType.IsPointer ||
                parameter.ParameterType.IsByRefLike ||
                parameter.IsOptional))
        {
            throw new ArgumentException("Expression functions cannot have ref, pointer, by-ref-like, or optional parameters.", nameof(function));
        }

        if (function.IsParameterArray &&
            (function.Parameters[^1].ParameterType is not { IsArray: true } parameterArray ||
                parameterArray.GetArrayRank() != 1))
        {
            throw new ArgumentException("Expression functions only support one-dimensional params arrays.", nameof(function));
        }
    }

    private static bool HasCompatibleParameterList(MethodInfo first, MethodInfo second)
    {
        var firstParameters = first.GetParameters();
        var secondParameters = second.GetParameters();
        return firstParameters.Length == secondParameters.Length &&
            firstParameters.Select(static parameter => parameter.ParameterType)
                .SequenceEqual(secondParameters.Select(static parameter => parameter.ParameterType));
    }

    private void RegisterBuiltIns()
    {
        BuiltInFunctions.Register(this);
    }
}

internal sealed class RegisteredFunction
{
    internal Delegate? Function { get; }

    internal MethodInfo Method { get; }

    internal ParameterInfo[] Parameters { get; }

    internal Type ReturnType => Method.ReturnType;

    internal bool IsGenericDefinition => Method.IsGenericMethodDefinition;

    internal bool IsParameterArray { get; }

    private RegisteredFunction(Delegate? function, MethodInfo method, ParameterInfo[] parameters, bool isParameterArray)
    {
        Function = function;
        Method = method;
        Parameters = parameters;
        IsParameterArray = isParameterArray;
    }

    internal static RegisteredFunction FromDelegate(Delegate function, MethodInfo invoke, MethodInfo parameterSource)
    {
        var sourceParameters = parameterSource.GetParameters();
        var invokeParameters = invoke.GetParameters();
        return new RegisteredFunction(
            function,
            invoke,
            invokeParameters,
            sourceParameters.LastOrDefault()?.GetCustomAttribute<ParamArrayAttribute>() is not null ||
            invokeParameters.LastOrDefault()?.GetCustomAttribute<ParamArrayAttribute>() is not null);
    }

    internal static RegisteredFunction FromStaticMethod(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return new RegisteredFunction(
            null,
            method,
            parameters,
            parameters.LastOrDefault()?.GetCustomAttribute<ParamArrayAttribute>() is not null);
    }

    internal bool HasSameSignature(RegisteredFunction other)
    {
        if (Method.GetGenericArguments().Length != other.Method.GetGenericArguments().Length ||
            IsParameterArray != other.IsParameterArray ||
            Parameters.Length != other.Parameters.Length)
        {
            return false;
        }

        return Parameters.Zip(other.Parameters)
            .All(pair => HaveEquivalentSignatureType(pair.First.ParameterType, pair.Second.ParameterType));
    }

    private static bool HaveEquivalentSignatureType(Type first, Type second)
    {
        if (first.IsGenericParameter || second.IsGenericParameter)
        {
            return first.IsGenericParameter &&
                second.IsGenericParameter &&
                first.GenericParameterPosition == second.GenericParameterPosition;
        }

        if (first.IsArray || second.IsArray)
        {
            return first.IsArray &&
                second.IsArray &&
                first.GetArrayRank() == second.GetArrayRank() &&
                HaveEquivalentSignatureType(first.GetElementType()!, second.GetElementType()!);
        }

        if (first.IsGenericType || second.IsGenericType)
        {
            return first.IsGenericType &&
                second.IsGenericType &&
                first.GetGenericTypeDefinition() == second.GetGenericTypeDefinition() &&
                first.GetGenericArguments().Zip(second.GetGenericArguments())
                    .All(pair => HaveEquivalentSignatureType(pair.First, pair.Second));
        }

        return first == second;
    }

    internal string GetSignature() =>
        $"({string.Join(", ", Parameters.Select(static parameter => parameter.ParameterType.Name))})";
}

internal readonly record struct FunctionRegistrySnapshot(
    int Version,
    IReadOnlyList<RegisteredFunction> Functions
);
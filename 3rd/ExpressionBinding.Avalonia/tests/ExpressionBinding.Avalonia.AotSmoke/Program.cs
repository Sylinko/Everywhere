using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Data.Converters;
using ExpressionBinding.Avalonia;

var registry = new ExpressionRegistry();
registry.RegisterFunction("kind", (Func<int, string>)(static _ => "int"));
registry.RegisterFunction("kind", (Func<double, string>)(static _ => "double"));
registry.RegisterFunction(
    "fallback",
    (Func<object?, object?, object?>)(static (value, alternative) =>
        ReferenceEquals(value, AvaloniaProperty.UnsetValue) ? alternative : value));

var conditional = CreateConverter("kind(A ? B : C)", registry);
AssertEqual("int", Convert(conditional, true, 1, AvaloniaProperty.UnsetValue));
AssertEqual("double", Convert(conditional, false, AvaloniaProperty.UnsetValue, 2d));
AssertEqual("int", Convert(conditional, true, 3, AvaloniaProperty.UnsetValue));

var shortCircuit = CreateConverter("A && B ? C : D", registry);
AssertEqual("selected", Convert(shortCircuit, true, true, "selected", "fallback"));
AssertEqual("fallback", Convert(shortCircuit, false, AvaloniaProperty.UnsetValue, "selected", "fallback"));

var fallback = CreateConverter("fallback(A, B)", registry);
AssertEqual(42, Convert(fallback, AvaloniaProperty.UnsetValue, 42));

var versionedRegistry = new ExpressionRegistry();
versionedRegistry.RegisterFunction("adjust", (Func<double, double>)(static value => value * 2));
var versioned = CreateConverter("adjust(A)", versionedRegistry);
AssertEqual(6d, Convert(versioned, 3));
versionedRegistry.RegisterFunction("adjust", (Func<int, int>)(static value => value + 1));
AssertEqual(4, Convert(versioned, 3));

Console.WriteLine("NativeAOT smoke test passed.");
return;

static IMultiValueConverter CreateConverter(string expression, ExpressionRegistry registry)
{
    var binding = new global::ExpressionBinding.Avalonia.ExpressionBinding
    {
        Expression = expression,
        Registry = registry
    };
    var multiBinding = (MultiBinding)binding.ProvideValue(EmptyServiceProvider.Instance);
    return multiBinding.Converter ?? throw new InvalidOperationException("Expression converter was not created.");
}

static object? Convert(IMultiValueConverter converter, params object?[] values) =>
    converter.Convert(values, typeof(object), null, CultureInfo.InvariantCulture);

static void AssertEqual(object? expected, object? actual)
{
    if (!Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', but received '{actual}'.");
    }
}

file sealed class EmptyServiceProvider : IServiceProvider
{
    internal static EmptyServiceProvider Instance { get; } = new();

    public object? GetService(Type serviceType) => null;
}

using System.Numerics;
using Avalonia;
using Vector = Avalonia.Vector;

namespace ExpressionBinding.Avalonia;

internal static class BuiltInFunctions
{
    internal static void Register(ExpressionRegistry registry)
    {
        RegisterNumericFunctions(registry);
        RegisterGeometryFunctions(registry);
    }

    private static void RegisterNumericFunctions(ExpressionRegistry registry)
    {
        registry.RegisterFunction("bool", static (bool value) => value);
        registry.RegisterFunction("bool", static (int value) => value != 0);
        registry.RegisterFunction("bool", static (long value) => value != 0);
        registry.RegisterFunction("bool", static (float value) => value != 0);
        registry.RegisterFunction("bool", static (double value) => value != 0);
        registry.RegisterFunction("bool", static (decimal value) => value != 0);

        registry.RegisterFunction("pow", (Func<float, float, float>)MathF.Pow);
        registry.RegisterFunction("pow", (Func<double, double, double>)Math.Pow);

        registry.RegisterFunction("abs", (Func<int, int>)Math.Abs);
        registry.RegisterFunction("abs", (Func<long, long>)Math.Abs);
        registry.RegisterFunction("abs", (Func<float, float>)Math.Abs);
        registry.RegisterFunction("abs", (Func<double, double>)Math.Abs);
        registry.RegisterFunction("abs", (Func<decimal, decimal>)Math.Abs);

        registry.RegisterFunction("ceil", (Func<float, float>)MathF.Ceiling);
        registry.RegisterFunction("ceil", (Func<double, double>)Math.Ceiling);
        registry.RegisterFunction("ceil", (Func<decimal, decimal>)Math.Ceiling);

        registry.RegisterFunction("clamp", (Func<int, int, int, int>)Math.Clamp);
        registry.RegisterFunction("clamp", (Func<long, long, long, long>)Math.Clamp);
        registry.RegisterFunction("clamp", (Func<float, float, float, float>)Math.Clamp);
        registry.RegisterFunction("clamp", (Func<double, double, double, double>)Math.Clamp);
        registry.RegisterFunction("clamp", (Func<decimal, decimal, decimal, decimal>)Math.Clamp);

        registry.RegisterFunction("floor", (Func<float, float>)MathF.Floor);
        registry.RegisterFunction("floor", (Func<double, double>)Math.Floor);
        registry.RegisterFunction("floor", (Func<decimal, decimal>)Math.Floor);

        registry.RegisterFunction("log", (Func<float, float>)MathF.Log);
        registry.RegisterFunction("log", (Func<double, double>)Math.Log);

        registry.RegisterFunction("max", (Func<int, int, int>)Math.Max);
        registry.RegisterFunction("max", (Func<long, long, long>)Math.Max);
        registry.RegisterFunction("max", (Func<float, float, float>)Math.Max);
        registry.RegisterFunction("max", (Func<double, double, double>)Math.Max);
        registry.RegisterFunction("max", (Func<decimal, decimal, decimal>)Math.Max);
        registry.RegisterFunction("max", (Func<int[], int>)Max);
        registry.RegisterFunction("max", (Func<long[], long>)Max);
        registry.RegisterFunction("max", (Func<float[], float>)Max);
        registry.RegisterFunction("max", (Func<double[], double>)Max);
        registry.RegisterFunction("max", (Func<decimal[], decimal>)Max);

        registry.RegisterFunction("min", (Func<int, int, int>)Math.Min);
        registry.RegisterFunction("min", (Func<long, long, long>)Math.Min);
        registry.RegisterFunction("min", (Func<float, float, float>)Math.Min);
        registry.RegisterFunction("min", (Func<double, double, double>)Math.Min);
        registry.RegisterFunction("min", (Func<decimal, decimal, decimal>)Math.Min);
        registry.RegisterFunction("min", (Func<int[], int>)Min);
        registry.RegisterFunction("min", (Func<long[], long>)Min);
        registry.RegisterFunction("min", (Func<float[], float>)Min);
        registry.RegisterFunction("min", (Func<double[], double>)Min);
        registry.RegisterFunction("min", (Func<decimal[], decimal>)Min);

        registry.RegisterFunction("round", (Func<float, float>)MathF.Round);
        registry.RegisterFunction("round", (Func<double, double>)Math.Round);
        registry.RegisterFunction("round", (Func<decimal, decimal>)Math.Round);

        registry.RegisterFunction("sign", (Func<int, int>)Math.Sign);
        registry.RegisterFunction("sign", (Func<long, int>)Math.Sign);
        registry.RegisterFunction("sign", (Func<float, int>)MathF.Sign);
        registry.RegisterFunction("sign", (Func<double, int>)Math.Sign);
        registry.RegisterFunction("sign", (Func<decimal, int>)Math.Sign);

        registry.RegisterFunction("truncate", (Func<float, float>)MathF.Truncate);
        registry.RegisterFunction("truncate", (Func<double, double>)Math.Truncate);
        registry.RegisterFunction("truncate", (Func<decimal, decimal>)Math.Truncate);
    }

    private static void RegisterGeometryFunctions(ExpressionRegistry registry)
    {
        registry.RegisterFunction("thickness", static (double uniform) => new Thickness(uniform));
        registry.RegisterFunction("thickness", static (double horizontal, double vertical) => new Thickness(horizontal, vertical));
        registry.RegisterFunction(
            "thickness",
            static (double left, double top, double right, double bottom) => new Thickness(left, top, right, bottom));
        registry.RegisterFunction("cornerRadius", static (double uniform) => new CornerRadius(uniform));
        registry.RegisterFunction(
            "cornerRadius",
            static (double topLeft, double topRight, double bottomRight, double bottomLeft) =>
                new CornerRadius(topLeft, topRight, bottomRight, bottomLeft));
        registry.RegisterFunction("size", static (double width, double height) => new Size(width, height));
        registry.RegisterFunction("point", static (double x, double y) => new Point(x, y));
        registry.RegisterFunction("vector", static (double x, double y) => new Vector(x, y));
    }

    private static T Min<T>(params T[] values) where T : INumber<T>
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var result = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            result = T.Min(result, values[i]);
        }

        return result;
    }

    private static T Max<T>(params T[] values) where T : INumber<T>
    {
        if (values.Length == 0)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        var result = values[0];
        for (var i = 1; i < values.Length; i++)
        {
            result = T.Max(result, values[i]);
        }

        return result;
    }
}
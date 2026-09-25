using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using ExpressionBinding.Avalonia.Parsing;

namespace ExpressionBinding.Avalonia.Binding;

internal sealed class ExpressionConverter : IMultiValueConverter
{
    private readonly string _expression;
    private readonly Func<IList<object?>, object?> _plan;

    internal ExpressionConverter(string expression, ExpressionSyntax syntax, ExpressionRegistry registry)
    {
        _expression = expression;
        _plan = ExpressionPlanCompiler.Compile(syntax, registry);
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var value in values)
        {
            if (ReferenceEquals(value, BindingOperations.DoNothing))
            {
                return BindingOperations.DoNothing;
            }
        }

        try
        {
            return _plan(values);
        }
        catch (Exception exception)
        {
            var error = exception is ExpressionBindingException ?
                exception :
                new ExpressionBindingException($"Failed to evaluate expression '{_expression}'.", exception);
            return new BindingNotification(error, BindingErrorType.Error);
        }
    }
}
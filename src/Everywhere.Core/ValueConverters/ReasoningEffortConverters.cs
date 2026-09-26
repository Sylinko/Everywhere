using System.Globalization;
using Avalonia.Data.Converters;
using Everywhere.AI;

namespace Everywhere.ValueConverters;

/// <summary>
/// Provides value converters for reasoning effort resolution in the Everywhere application.
/// </summary>
public static class ReasoningEffortConverters
{
    /// <summary>
    /// Gets a multi-value converter that resolves the effective reasoning effort value based on user input, selected value, and default values.
    /// </summary>
    public static IMultiValueConverter EffectiveValue { get; } = new EffectiveValueConverter();

    private sealed class EffectiveValueConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count != 3 || values.AsValueEnumerable().Any(value => value == AvaloniaProperty.UnsetValue))
            {
                return AvaloniaProperty.UnsetValue;
            }

            var resolution = ReasoningEffortResolver.Resolve(
                values[0] as string,
                values[1] as string,
                values[2] as IReadOnlyList<string>);
            return resolution.EffectiveValue;
        }
    }
}
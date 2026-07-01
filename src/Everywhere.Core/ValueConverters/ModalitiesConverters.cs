using System.Globalization;
using Avalonia.Data.Converters;
using Everywhere.AI;

namespace Everywhere.ValueConverters;

public static class ModalitiesConverters
{
    public static IMultiValueConverter IsSupported { get; } = new IsSupportedConverter();

    public static IValueConverter SupportsImage { get; } = new FuncValueConverter<Modalities, bool>(m => m.SupportsImage);

    private sealed class IsSupportedConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is not [Modalities supportedModalities, Modalities requiredModalities])
                return false;

            // Check flags
            return (supportedModalities & requiredModalities) == requiredModalities;
        }
    }
}
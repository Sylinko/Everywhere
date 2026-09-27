using System.Globalization;

namespace Everywhere.Automation;

internal static class VisualTextLengthFormatting
{
    public static VisualTextLength EnsureLowerBound(VisualTextLength measurement, int lowerBound)
    {
        if (measurement.Kind == VisualTextLengthKind.LowerBound)
        {
            return VisualTextLength.LowerBound(Math.Max(measurement.Value, lowerBound));
        }

        if (measurement.Value < lowerBound)
        {
            return VisualTextLength.LowerBound(lowerBound);
        }

        return measurement;
    }

    public static VisualTextLengthKind Combine(VisualTextLengthKind current, VisualTextLengthKind next)
    {
        return current == VisualTextLengthKind.LowerBound || next == VisualTextLengthKind.LowerBound ?
            VisualTextLengthKind.LowerBound :
            VisualTextLengthKind.Exact;
    }

    public static string Format(VisualTextLength measurement) => measurement.Kind switch
    {
        VisualTextLengthKind.Exact => measurement.Value.ToString(CultureInfo.InvariantCulture),
        VisualTextLengthKind.LowerBound => $"≥{measurement.Value.ToString(CultureInfo.InvariantCulture)}",
        _ => throw new ArgumentOutOfRangeException(nameof(measurement), measurement, null),
    };

    public static int SaturatingIncrement(int value) => value == int.MaxValue ? int.MaxValue : value + 1;
}
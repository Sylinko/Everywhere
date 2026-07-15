using System.Globalization;
using Avalonia.Data.Converters;
using Everywhere.Interactions;

namespace Everywhere.ValueConverters;

/// <summary>
/// Computes and formats a localized, human-readable elapsed duration at binding evaluation time.
/// Pair with <see cref="PeriodicRefreshTextBehavior"/> when the running value must remain live.
/// </summary>
public static class LiveElapsedConverters
{
    private const long SecondsPerMinute = 60;
    private const long SecondsPerHour = 60 * SecondsPerMinute;
    private const long SecondsPerDay = 24 * SecondsPerHour;

    /// <summary>
    /// Converts a creation time, an optional finish time, and an optional live-state flag into a
    /// localized duration. Durations below one minute retain tenths of a second; longer durations
    /// use the two largest relevant units to remain compact in activity headers.
    /// </summary>
    public static IMultiValueConverter ToDisplayText { get; } = new ToDisplayTextConverter();

    private sealed class ToDisplayTextConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.Count < 2 || values[0] is not DateTimeOffset createdAt) return string.Empty;

            // Some callers assign FinishedAt immediately before clearing IsBusy. Treating the
            // optional third value as authoritative keeps the displayed duration live during that
            // short interval instead of momentarily freezing at the premature endpoint.
            DateTimeOffset? finishedAt = values.Count > 2 && values[2] is true ? null
                : values[1] is DateTimeOffset value ? value : null;

            var totalSeconds = Math.Max(((finishedAt ?? DateTimeOffset.UtcNow) - createdAt).TotalSeconds, 0d);
            return Format(totalSeconds, culture);
        }
    }

    private static string Format(double totalSeconds, CultureInfo culture)
    {
        if (totalSeconds < SecondsPerMinute)
        {
            // Truncation prevents 59.96 seconds from being rounded to the misleading "60.0s"
            // immediately before the converter switches to the minute-and-second representation.
            var truncatedTenths = Math.Truncate(totalSeconds * 10d) / 10d;
            return string.Format(culture, LocaleResolver.LiveElapsedConverters_SecondsFormat, truncatedTenths);
        }

        var wholeSeconds = (long)Math.Floor(totalSeconds);
        if (wholeSeconds < SecondsPerHour)
        {
            var minutes = wholeSeconds / SecondsPerMinute;
            var seconds = wholeSeconds % SecondsPerMinute;
            return string.Format(culture, LocaleResolver.LiveElapsedConverters_MinutesSecondsFormat, minutes, seconds);
        }

        if (wholeSeconds < SecondsPerDay)
        {
            var hours = wholeSeconds / SecondsPerHour;
            var minutes = wholeSeconds % SecondsPerHour / SecondsPerMinute;
            return string.Format(culture, LocaleResolver.LiveElapsedConverters_HoursMinutesFormat, hours, minutes);
        }

        var days = wholeSeconds / SecondsPerDay;
        var remainingHours = wholeSeconds % SecondsPerDay / SecondsPerHour;
        return string.Format(culture, LocaleResolver.LiveElapsedConverters_DaysHoursFormat, days, remainingHours);
    }
}
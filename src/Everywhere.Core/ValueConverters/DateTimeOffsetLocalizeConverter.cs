using System.Globalization;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public static class DateTimeOffsetConverters
{
    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to local time for display, and back to UTC for storage.
    /// </summary>
    public static IValueConverter Localize { get; } =
        new FuncValueConverter<DateTimeOffset?, DateTimeOffset?>(value => value?.ToLocalTime(), value => value?.ToUniversalTime());

    /// <summary>
    /// Converts a <see cref="DateTimeOffset"/> to a short date or time string for display, depending on how close it is to the current time.
    /// </summary>
    /// <remarks>
    /// If the value is within 12 hours in the future, it will be displayed as a short time (e.g. "14:30").
    /// If the value is within the same day in the past, it will also be displayed as a short time (e.g. "09:15").
    /// Otherwise, it will be displayed as a short date (e.g. "Mar 5") using the current culture's short month and day pattern.
    /// </remarks>
    public static IValueConverter ToShortDateOrTimeString { get; } = new FuncValueConverter<DateTimeOffset?, string?>(value =>
    {
        if (value is null)
        {
            return string.Empty;
        }

        var culture = CultureInfo.CurrentUICulture;
        var localValue = value.Value.ToLocalTime();
        var now = DateTimeOffset.Now;

        var remaining = value.Value - now;

        if (remaining >= TimeSpan.Zero && remaining < TimeSpan.FromHours(12))
        {
            return localValue.ToString("HH:mm", culture);
        }

        if (remaining < TimeSpan.Zero && localValue.Date == now.Date)
        {
            return localValue.ToString("HH:mm", culture);
        }

        return localValue.ToString(Abstractions.I18N.LocaleResolver.Common_ShortMonthDayPattern, culture);
    });
}
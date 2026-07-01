namespace Everywhere.Common;

public enum HumanizedDate
{
    [DynamicLocaleKey(LocaleKey.HumanizedDate_Today)]
    Today,
    [DynamicLocaleKey(LocaleKey.HumanizedDate_Yesterday)]
    Yesterday,
    [DynamicLocaleKey(LocaleKey.HumanizedDate_LastWeek)]
    LastWeek,
    [DynamicLocaleKey(LocaleKey.HumanizedDate_LastMonth)]
    LastMonth,
    [DynamicLocaleKey(LocaleKey.HumanizedDate_LastYear)]
    LastYear,
    [DynamicLocaleKey(LocaleKey.HumanizedDate_Earlier)]
    Earlier
}
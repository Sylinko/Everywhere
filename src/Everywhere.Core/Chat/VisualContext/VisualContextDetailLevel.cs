namespace Everywhere.Chat;

public enum VisualContextDetailLevel
{
    [DynamicLocaleKey(LocaleKey.VisualContextDetailLevel_Minimal)]
    Minimal = 0,

    [DynamicLocaleKey(LocaleKey.VisualContextDetailLevel_Compact)]
    Compact = 1,

    [DynamicLocaleKey(LocaleKey.VisualContextDetailLevel_Detailed)]
    Detailed = 2,
}
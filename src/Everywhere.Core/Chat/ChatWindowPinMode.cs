namespace Everywhere.Chat;

public enum ChatWindowPinMode
{
    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_RememberPrevious)]
    RememberLast,

    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_AlwaysTopmost)]
    AlwaysTopmost,

    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_AlwaysPinned)]
    AlwaysPinned,

    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_AlwaysUnpinned)]
    AlwaysUnpinned,

    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_TopmostOnInput)]
    TopmostOnInput,

    [DynamicLocaleKey(LocaleKey.ChatWindowPinMode_PinOnInput)]
    PinOnInput
}
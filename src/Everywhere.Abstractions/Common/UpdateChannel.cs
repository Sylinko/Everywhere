namespace Everywhere.Common;

public enum UpdateChannel
{
    [DynamicLocaleKey(LocaleKey.UpdateChannel_Unknown)]
    Unknown = 0,

    [DynamicLocaleKey(LocaleKey.UpdateChannel_Canary)]
    Canary = 1,

    // Alpha = 2,
    // Beta = 3,
    // RC = 4,

    [DynamicLocaleKey(LocaleKey.UpdateChannel_Stable)]
    Stable = 99
}
namespace Everywhere.Common;

public enum UpdateChannel
{
    [DynamicResourceKey(LocaleKey.UpdateChannel_Unknown)]
    Unknown = 0,

    [DynamicResourceKey(LocaleKey.UpdateChannel_Canary)]
    Canary = 1,

    // Alpha = 2,
    // Beta = 3,
    // RC = 4,

    [DynamicResourceKey(LocaleKey.UpdateChannel_Stable)]
    Stable = 99
}
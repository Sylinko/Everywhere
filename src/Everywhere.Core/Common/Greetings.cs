using Everywhere.Cloud;

namespace Everywhere.Common;

/// <summary>
/// Common greeting messages. Including Tips and holiday greetings.
/// </summary>
public class Greetings(ICloudClient cloudClient) : IGreetings
{
    private static ReadOnlySpan<string> TipKeys => new[]
    {
        LocaleKey.Greetings_Tip1,
        LocaleKey.Greetings_Tip2,
        LocaleKey.Greetings_Tip3,
        LocaleKey.Greetings_Tip4,
        LocaleKey.Greetings_Tip5,
        LocaleKey.Greetings_Tip6,
    };

    private static ReadOnlySpan<string> NotLoginTipKeys => new[]
    {
        LocaleKey.Greetings_Tip1,
        LocaleKey.Greetings_Tip2,
        LocaleKey.Greetings_Tip3,
        LocaleKey.Greetings_Tip4,
        LocaleKey.Greetings_Tip5,
        LocaleKey.Greetings_Tip6,
        LocaleKey.Greetings_NotLoginTip1,
        LocaleKey.Greetings_NotLoginTip2,
    };

    public DynamicLocaleKey GetRandomTip()
    {
        var isLogin = cloudClient.UserProfile is not null;
        var tips = isLogin ? TipKeys : NotLoginTipKeys;
        return new DynamicLocaleKey(tips[Random.Shared.Next(tips.Length)]);
    }
}
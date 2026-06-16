using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

public static class MaxContextRoundsConverters
{
    public static IValueConverter ToDisplayKey { get; } = new BidirectionalFuncValueConverter<int, IDynamicLocaleKey>(
        convert: static (value, _) => value switch
        {
            -1 => new DynamicLocaleKey(LocaleKey.PersistentState_MaxContextRounds_Value_Unlimited),
            0 => new DynamicLocaleKey(LocaleKey.PersistentState_MaxContextRounds_Value_CurrentInputOnly),
            _ => new DirectLocaleKey(value.ToString())
        },
        convertBack: static (_, _) => throw new NotSupportedException());
}
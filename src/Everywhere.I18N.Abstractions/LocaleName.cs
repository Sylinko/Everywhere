using System.ComponentModel;
using System.Globalization;

namespace Everywhere.I18N;

[TypeConverter(typeof(LocaleNameTypeConverter))]
public enum LocaleName
{
    En,
    ZhHans,
    De,
    Es,
    Fr,
    It,
    Ja,
    Ko,
    PtBr,
    Ru,
    ZhHant,
    ZhHantHk,
    Tr,
}

public class LocaleNameTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string strValue)
        {
            return Enum.TryParse<LocaleName>(strValue, true, out var locale) ? locale : default(LocaleName);
        }

        return base.ConvertFrom(context, culture, value);
    }
}

public static class LocaleNameExtensions
{
    extension(LocaleName localeName)
    {
        public string ToNativeName()
        {
            return localeName switch
            {
                LocaleName.En => "English",
                LocaleName.ZhHans => "中文（简体）",
                LocaleName.De => "Deutsch",
                LocaleName.Es => "español",
                LocaleName.Fr => "français",
                LocaleName.It => "italiano",
                LocaleName.Ja => "日本語",
                LocaleName.Ko => "한국어",
                LocaleName.PtBr => "português (Brasil)",
                LocaleName.Ru => "русский",
                LocaleName.ZhHant => "中文（繁體）",
                LocaleName.ZhHantHk => "中文（香港）",
                LocaleName.Tr => "Türkçe",
                _ => "English",
            };
        }

        public string ToEnglishName()
        {
            return localeName switch
            {
                LocaleName.En => "English",
                LocaleName.ZhHans => "Chinese (Simplified)",
                LocaleName.De => "German",
                LocaleName.Es => "Spanish",
                LocaleName.Fr => "French",
                LocaleName.It => "Italian",
                LocaleName.Ja => "Japanese",
                LocaleName.Ko => "Korean",
                LocaleName.PtBr => "Portuguese (Brazil)",
                LocaleName.Ru => "Russian",
                LocaleName.ZhHant => "Chinese (Traditional)",
                LocaleName.ZhHantHk => "Chinese (Traditional, Hong Kong SAR)",
                LocaleName.Tr => "Turkish",
                _ => "English",
            };
        }
    }

}

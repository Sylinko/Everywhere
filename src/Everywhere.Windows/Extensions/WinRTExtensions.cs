using Windows.Globalization;
using Everywhere.I18N;

namespace Everywhere.Windows.Extensions;

public static class WinRTExtensions
{
    public static string ToBcp47LanguageTag(this LocaleName localeName)
    {
        return localeName switch
        {
            LocaleName.En => "en-US",
            LocaleName.Ko => "ko-KR",
            LocaleName.Ru => "ru-RU",
            LocaleName.ZhHans => "zh-Hans-CN",
            LocaleName.Fr => "fr-FR",
            LocaleName.ZhHantHk => "zh-Hant-HK",
            LocaleName.ZhHant => "zh-Hant-TW",
            LocaleName.Tr => "tr-TR",
            LocaleName.De => "de-DE",
            LocaleName.It => "it-IT",
            LocaleName.Es => "es-ES",
            LocaleName.Ja => "ja-JP",
            _ => "en-US"
        };
    }

    public static Language ToWinRTLanguage(this LocaleName localeName)
    {
        return new Language(localeName.ToBcp47LanguageTag());
    }

    public static LocaleName ToLocaleName(this Language language)
    {
        return language.TryToLocaleName(out var localeName) ? localeName : LocaleName.En;
    }

    public static bool TryToLocaleName(this Language? language, out LocaleName localeName)
    {
        if (language is not null) return TryParseLocaleName(language.LanguageTag, out localeName);

        localeName = LocaleName.En;
        return false;
    }

    public static bool TryParseLocaleName(string? languageTag, out LocaleName localeName)
    {
        localeName = LocaleName.En;

        if (string.IsNullOrWhiteSpace(languageTag)) return false;

        var tagParts = languageTag.Trim().Split(['-', '_'], 2, StringSplitOptions.RemoveEmptyEntries);
        if (tagParts.Length == 0) return false;

        switch (tagParts[0].ToLower())
        {
            case "de":
                localeName = LocaleName.De;
                return true;
            case "en":
                localeName = LocaleName.En;
                return true;
            case "es":
                localeName = LocaleName.Es;
                return true;
            case "fr":
                localeName = LocaleName.Fr;
                return true;
            case "it":
                localeName = LocaleName.It;
                return true;
            case "ja":
                localeName = LocaleName.Ja;
                return true;
            case "ko":
                localeName = LocaleName.Ko;
                return true;
            case "ru":
                localeName = LocaleName.Ru;
                return true;
            case "tr":
                localeName = LocaleName.Tr;
                return true;
            case "zh":
            {
                if (tagParts.Length == 1)
                {
                    localeName = LocaleName.ZhHans;
                    return true;
                }

                if (tagParts[1].Contains("hk", StringComparison.OrdinalIgnoreCase) ||
                    tagParts[1].Contains("mo", StringComparison.OrdinalIgnoreCase))
                {
                    localeName = LocaleName.ZhHantHk;
                    return true;
                }

                if (tagParts[1].Contains("hant", StringComparison.OrdinalIgnoreCase) ||
                    tagParts[1].Contains("tw", StringComparison.OrdinalIgnoreCase))
                {
                    localeName = LocaleName.ZhHant;
                    return true;
                }

                localeName = LocaleName.ZhHans;
                return true;
            }
        }

        return false;
    }
}
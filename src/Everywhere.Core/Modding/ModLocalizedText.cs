using System.Diagnostics.CodeAnalysis;
using Everywhere.Modding.Hosting.Management;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding;

/// <summary>
/// Resolves localized mod metadata, source display labels, and icon URIs across package manifests and repository catalogs.
/// </summary>
public static class ModLocalizedText
{
    /// <summary>
    /// Resolves a localized value for the current (or specified) application locale, returning <paramref name="fallback"/> when empty.
    /// </summary>
    public static string Resolve(IReadOnlyDictionary<string, string>? translations, string fallback, LocaleName? locale = null) =>
        ResolveEntry(translations, locale) ?? fallback;

    /// <summary>
    /// Resolves the best matching non-empty localized entry using BCP-47 culture fallback rules.
    /// </summary>
    public static string? ResolveEntry(IReadOnlyDictionary<string, string>? translations, LocaleName? locale = null)
    {
        if (translations is not { Count: > 0 }) return null;

        var targetLocale = locale ?? LocaleManager.CurrentLocale;
        var culture = targetLocale.ToCultureInfo();

        if (TryGetNonEmpty(translations, culture.Name, out var exact)) return exact;

        foreach (var alias in GetLocaleAliases(targetLocale))
        {
            if (TryGetNonEmpty(translations, alias, out var matched)) return matched;
        }

        var parent = culture.Parent;
        while (!string.IsNullOrEmpty(parent.Name))
        {
            if (TryGetNonEmpty(translations, parent.Name, out var parentValue)) return parentValue;
            parent = parent.Parent;
        }

        return TryGetNonEmpty(translations, "en", out var english)
            ? english
            : translations.Values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    /// <summary>
    /// Resolves a renderable icon URI for either an installed package directory or a remote/local repository entry.
    /// </summary>
    public static string? ResolveIconSource(ModSummary? installed, ModIndexEntry? catalogEntry, ModSourceInfo? source)
    {
        if (installed?.IconPath is { Length: > 0 } localIcon && File.Exists(localIcon))
        {
            return new Uri(Path.GetFullPath(localIcon)).AbsoluteUri;
        }

        var catalogIcon = catalogEntry?.Icon;
        if (string.IsNullOrWhiteSpace(catalogIcon)) return null;

        if (catalogIcon.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            catalogIcon.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            catalogIcon.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            return catalogIcon;
        }

        if (source is null) return null;

        if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var sourceUri))
        {
            return ResolveLocalIconUri(source.Url, catalogIcon);
        }

        if (sourceUri.Scheme is "http" or "https")
        {
            return Uri.TryCreate(sourceUri, catalogIcon, out var resolved) && resolved.Scheme is "http" or "https"
                ? resolved.AbsoluteUri
                : null;
        }

        return sourceUri.IsFile
            ? ResolveLocalIconUri(sourceUri.LocalPath, catalogIcon)
            : ResolveLocalIconUri(source.Url, catalogIcon);
    }

    /// <summary>
    /// Returns a dynamic localization key representing a built-in, local-import, or user-defined source identifier.
    /// </summary>
    public static IDynamicLocaleKey CreateSourceDisplayNameKey(string sourceId) => sourceId switch
    {
        "builtin" => new DynamicLocaleKey(LocaleKey.ModPage_Source_BuiltIn),
        "official" => new DynamicLocaleKey(LocaleKey.ModPage_Source_Official),
        ModStateRecord.LocalZipSourceId => new DynamicLocaleKey(LocaleKey.ModPage_Source_LocalZip),
        ModStateRecord.LocalDirectorySourceId => new DynamicLocaleKey(LocaleKey.ModPage_Source_LocalDir),
        _ => new DirectLocaleKey(sourceId)
    };

    private static string? ResolveLocalIconUri(string sourceLocation, string relativeIcon)
    {
        try
        {
            var baseDirectory = sourceLocation.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(Path.GetFullPath(sourceLocation))
                : Path.GetFullPath(sourceLocation);
            if (string.IsNullOrWhiteSpace(baseDirectory) || !Directory.Exists(baseDirectory)) return null;

            var candidate = PackagePath.Resolve(baseDirectory, relativeIcon);
            return File.Exists(candidate) ? new Uri(candidate).AbsoluteUri : null;
        }
        catch (Exception exception) when (exception is ModValidationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    private static IEnumerable<string> GetLocaleAliases(LocaleName locale) => locale switch
    {
        LocaleName.ZhHans => ["zh-CN", "zh-SG", "zh"],
        LocaleName.ZhHantHk => ["zh-HK", "zh-Hant", "zh-TW", "zh-MO", "zh"],
        LocaleName.ZhHant => ["zh-TW", "zh-MO", "zh-HK", "zh"],
        LocaleName.PtBr => ["pt"],
        _ => []
    };

    private static bool TryGetNonEmpty(
        IReadOnlyDictionary<string, string> translations,
        string key,
        [NotNullWhen(true)] out string? value
    )
    {
        if (translations.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            value = raw;
            return true;
        }

        foreach (var pair in translations)
        {
            if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(pair.Value))
            {
                continue;
            }

            value = pair.Value;
            return true;
        }

        value = null;
        return false;
    }
}

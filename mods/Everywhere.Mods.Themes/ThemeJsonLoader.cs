using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Microsoft.Extensions.Logging;
using TextMateSharp.Grammars;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Discovers and parses lightweight JSON theme definition files from a directory using
/// source-generated <see cref="System.Text.Json"/> metadata.
/// </summary>
internal static partial class ThemeJsonLoader
{
    public static IReadOnlyList<ThemeDefinition> LoadFromDirectory(string directoryPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
        {
            return [];
        }

        var results = new List<ThemeDefinition>();
        var files = Directory.EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in files)
        {
            try
            {
                var fileText = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(fileText))
                {
                    continue;
                }

                var document = JsonSerializer.Deserialize(fileText, ThemeJsonSerializerContext.Default.ThemeJsonFile);
                if (document is null)
                {
                    continue;
                }

                if (document.Themes is { Length: > 0 } multiThemes)
                {
                    foreach (var entry in multiThemes)
                    {
                        if (TryConvert(entry, filePath, logger, out var parsed))
                        {
                            results.Add(parsed);
                        }
                    }
                }
                else if (TryConvert(document, filePath, logger, out var single))
                {
                    results.Add(single);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to load JSON theme file '{ThemeFilePath}'.", filePath);
            }
        }

        return results;
    }

    private static bool TryConvert(
        ThemeJsonEntry entry,
        string filePath,
        ILogger logger,
        out ThemeDefinition theme
    )
    {
        theme = null!;
        if (string.IsNullOrWhiteSpace(entry.Id) || entry.Palette is null)
        {
            logger.LogWarning("Skipping JSON theme in '{ThemeFilePath}': missing required 'id' or 'palette'.", filePath);
            return false;
        }

        if (!TryParseVariant(entry.Variant, out var variant))
        {
            logger.LogWarning(
                "Skipping JSON theme '{ThemeId}' in '{ThemeFilePath}': unsupported variant '{Variant}'. Expected 'light' or 'dark'.",
                entry.Id,
                filePath,
                entry.Variant);
            return false;
        }

        if (!TryBuildPalette(entry.Palette, filePath, entry.Id, logger, out var palette))
        {
            return false;
        }

        ThemeName? codeTheme = null;
        if (!string.IsNullOrWhiteSpace(entry.CodeTheme))
        {
            if (Enum.TryParse<ThemeName>(entry.CodeTheme, ignoreCase: true, out var parsedCodeTheme))
            {
                codeTheme = parsedCodeTheme;
            }
            else
            {
                logger.LogWarning(
                    "JSON theme '{ThemeId}' in '{ThemeFilePath}' specified unknown codeTheme '{CodeTheme}'; falling back to variant default.",
                    entry.Id,
                    filePath,
                    entry.CodeTheme);
            }
        }

        Dictionary<string, Color>? overrides = null;
        if (entry.TokenOverrides is { Count: > 0 })
        {
            overrides = new Dictionary<string, Color>(StringComparer.Ordinal);
            foreach (var (key, rawColor) in entry.TokenOverrides)
            {
                if (Color.TryParse(rawColor, out var color))
                {
                    overrides[key] = color;
                }
                else
                {
                    logger.LogWarning(
                        "JSON theme '{ThemeId}' in '{ThemeFilePath}' has invalid tokenOverrides color '{ColorValue}' for '{TokenKey}'.",
                        entry.Id,
                        filePath,
                        rawColor,
                        key);
                }
            }
        }

        theme = new ThemeDefinition
        {
            Id = entry.Id.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Trim() : entry.Name.Trim(),
            Author = string.IsNullOrWhiteSpace(entry.Author) ? null : entry.Author.Trim(),
            Variant = variant,
            Palette = palette,
            CodeTheme = codeTheme,
            TokenOverrides = overrides,
            IsCustomJson = true,
        };
        return true;
    }

    private static bool TryParseVariant(string? rawVariant, out ThemeBaseVariant variant)
    {
        if (string.Equals(rawVariant, "light", StringComparison.OrdinalIgnoreCase))
        {
            variant = ThemeBaseVariant.Light;
            return true;
        }

        if (string.Equals(rawVariant, "dark", StringComparison.OrdinalIgnoreCase))
        {
            variant = ThemeBaseVariant.Dark;
            return true;
        }

        variant = default;
        return false;
    }

    private static bool TryBuildPalette(
        ThemeJsonPalette json,
        string filePath,
        string themeId,
        ILogger logger,
        out ThemePalette palette
    )
    {
        palette = null!;

        if (!TryParseRequiredColor(json.WindowBackground, nameof(json.WindowBackground), themeId, filePath, logger, out var windowBg) ||
            !TryParseRequiredColor(json.CardBackground, nameof(json.CardBackground), themeId, filePath, logger, out var cardBg) ||
            !TryParseRequiredColor(
                json.SecondaryCardBackground,
                nameof(json.SecondaryCardBackground),
                themeId,
                filePath,
                logger,
                out var secondaryCardBg) ||
            !TryParseRequiredColor(json.DialogBackground, nameof(json.DialogBackground), themeId, filePath, logger, out var dialogBg) ||
            !TryParseRequiredColor(json.InputBackground, nameof(json.InputBackground), themeId, filePath, logger, out var inputBg) ||
            !TryParseRequiredColor(json.Foreground, nameof(json.Foreground), themeId, filePath, logger, out var foreground) ||
            !TryParseRequiredColor(json.MutedForeground, nameof(json.MutedForeground), themeId, filePath, logger, out var mutedForeground) ||
            !TryParseRequiredColor(json.Border, nameof(json.Border), themeId, filePath, logger, out var border) ||
            !TryParseRequiredColor(json.Primary, nameof(json.Primary), themeId, filePath, logger, out var primary) ||
            !TryParseRequiredColor(json.Secondary, nameof(json.Secondary), themeId, filePath, logger, out var secondary) ||
            !TryParseRequiredColor(json.Destructive, nameof(json.Destructive), themeId, filePath, logger, out var destructive) ||
            !TryParseRequiredColor(json.Info, nameof(json.Info), themeId, filePath, logger, out var info) ||
            !TryParseRequiredColor(json.Success, nameof(json.Success), themeId, filePath, logger, out var success) ||
            !TryParseRequiredColor(json.Warning, nameof(json.Warning), themeId, filePath, logger, out var warning))
        {
            return false;
        }

        palette = new ThemePalette
        {
            WindowBackground = windowBg,
            CardBackground = cardBg,
            SecondaryCardBackground = secondaryCardBg,
            DialogBackground = dialogBg,
            InputBackground = inputBg,
            SidebarBackground = TryParseOptionalColor(json.SidebarBackground, nameof(json.SidebarBackground), themeId, filePath, logger),
            Foreground = foreground,
            MutedForeground = mutedForeground,
            Border = border,
            Outline = TryParseOptionalColor(json.Outline, nameof(json.Outline), themeId, filePath, logger),
            Primary = primary,
            Secondary = secondary,
            Destructive = destructive,
            Info = info,
            Success = success,
            Warning = warning,
            Error = TryParseOptionalColor(json.Error, nameof(json.Error), themeId, filePath, logger),
            Quote = TryParseOptionalColor(json.Quote, nameof(json.Quote), themeId, filePath, logger),
            Selection = TryParseOptionalColor(json.Selection, nameof(json.Selection), themeId, filePath, logger),
        };
        return true;
    }

    private static bool TryParseRequiredColor(
        string? rawValue,
        string propertyName,
        string themeId,
        string filePath,
        ILogger logger,
        out Color color
    )
    {
        if (!string.IsNullOrWhiteSpace(rawValue) && Color.TryParse(rawValue, out color))
        {
            return true;
        }

        logger.LogWarning(
            "Skipping JSON theme '{ThemeId}' in '{ThemeFilePath}': invalid or missing color for 'palette.{Property}'.",
            themeId,
            filePath,
            propertyName);
        color = default;
        return false;
    }

    private static Color? TryParseOptionalColor(
        string? rawValue,
        string propertyName,
        string themeId,
        string filePath,
        ILogger logger
    )
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return null;
        }

        if (Color.TryParse(rawValue, out var parsed))
        {
            return parsed;
        }

        logger.LogWarning(
            "JSON theme '{ThemeId}' in '{ThemeFilePath}' has invalid optional color '{ColorValue}' for 'palette.{Property}'.",
            themeId,
            filePath,
            rawValue,
            propertyName);
        return null;
    }

    internal class ThemeJsonEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Author { get; set; }
        public string? Variant { get; set; }
        public string? CodeTheme { get; set; }
        public ThemeJsonPalette? Palette { get; set; }
        public Dictionary<string, string>? TokenOverrides { get; set; }
    }

    internal sealed class ThemeJsonFile : ThemeJsonEntry
    {
        public ThemeJsonEntry[]? Themes { get; set; }
    }

    internal sealed class ThemeJsonPalette
    {
        public string? WindowBackground { get; set; }
        public string? CardBackground { get; set; }
        public string? SecondaryCardBackground { get; set; }
        public string? DialogBackground { get; set; }
        public string? InputBackground { get; set; }
        public string? SidebarBackground { get; set; }
        public string? Foreground { get; set; }
        public string? MutedForeground { get; set; }
        public string? Border { get; set; }
        public string? Outline { get; set; }
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
        public string? Destructive { get; set; }
        public string? Info { get; set; }
        public string? Success { get; set; }
        public string? Warning { get; set; }
        public string? Error { get; set; }
        public string? Quote { get; set; }
        public string? Selection { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true)]
    [JsonSerializable(typeof(ThemeJsonFile))]
    internal partial class ThemeJsonSerializerContext : JsonSerializerContext;
}

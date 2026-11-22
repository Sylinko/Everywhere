using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using TextMateSharp.Grammars;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Compiles a normalized <see cref="ThemeDefinition"/> into the full set of color,
/// compatibility, syntax-highlighting, and Markdown CSS resources expected by ShadUI and Everywhere.
/// </summary>
public static class ShadThemeCompiler
{
    private const double ForegroundPreference = 1.15;

    private static readonly Color LightFallbackInk = Color.FromRgb(253, 252, 249);
    private static readonly Color DarkFallbackInk = Color.FromRgb(16, 16, 20);

    /// <summary>
    /// Compiles the supplied <paramref name="theme"/> into a target <see cref="ResourceDictionary"/>.
    /// Host-default placeholder entries leave the dictionary empty so ShadUI's built-in defaults apply untouched.
    /// </summary>
    public static ResourceDictionary Compile(ThemeDefinition theme, Color? accentOverride = null)
    {
        ArgumentNullException.ThrowIfNull(theme);

        var target = new ResourceDictionary();
        CompileInto(theme, target, accentOverride);
        return target;
    }

    /// <summary>
    /// Populates <paramref name="target"/> with all compiled theme tokens for <paramref name="theme"/>.
    /// </summary>
    public static void CompileInto(ThemeDefinition theme, ResourceDictionary target, Color? accentOverride = null)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(target);

        if (theme.IsHostDefault)
        {
            target.Clear();
            return;
        }

        var isLight = theme.Variant == ThemeBaseVariant.Light;
        var palette = theme.Palette;
        var primary = accentOverride ?? palette.Primary;
        var sidebar = palette.SidebarBackground ?? palette.WindowBackground;
        var error = palette.Error ?? palette.Destructive;
        var quote = palette.Quote ?? palette.Warning;
        var outline = palette.Outline ?? Blend(palette.Border, palette.MutedForeground, 0.25);
        var muted = Blend(palette.MutedForeground, palette.Border, 0.15);
        var ghost = Blend(palette.WindowBackground, palette.Secondary, 0.55);

        var lightTextInk = isLight ? LightFallbackInk : palette.Foreground;
        var darkTextInk = isLight ? palette.Foreground : DarkFallbackInk;

        var primaryForeground = SelectContrastInk(primary, darkTextInk, lightTextInk, isLight);
        var primaryHover = ComputeHoverColor(primary, primaryForeground == darkTextInk ? Colors.White : Colors.Black);
        var destructiveForeground = SelectContrastInk(palette.Destructive, darkTextInk, lightTextInk, isLight);
        var badgeForeground = SelectContrastInk(palette.Info, darkTextInk, lightTextInk, isLight);

        var selection = palette.Selection ?? (isLight
            ? Blend(palette.WindowBackground, palette.Foreground, 0.06)
            : WithAlpha(palette.SecondaryCardBackground, 140));
        var selectionHover = isLight
            ? Blend(palette.WindowBackground, palette.Foreground, 0.10)
            : WithAlpha(palette.SecondaryCardBackground, 96);

        var entries = new Dictionary<object, object?>(96)
        {
            // Base surfaces & typography
            ["TransparentColor"] = isLight ? Color.FromArgb(0, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0),
            ["BackgroundColor"] = isLight ? Color.FromArgb(0, 255, 255, 255) : Color.FromArgb(0, 0, 0, 0),
            ["ForegroundColor"] = palette.Foreground,
            ["ForegroundLeadColor"] = palette.MutedForeground,
            ["MutedColor"] = muted,
            ["BorderColor"] = palette.Border,
            ["BorderColor60"] = WithAlpha(palette.Border, 153),
            ["BorderColor30"] = WithAlpha(palette.Border, 76),
            ["OutlineColor"] = outline,
            ["GhostColor"] = ghost,
            ["GhostHoverColor"] = WithAlpha(ghost, 127),
            ["GhostHoverColor50"] = WithAlpha(ghost, 51),
            ["SelectedTabBackgroundColor"] = isLight ? palette.CardBackground : palette.Secondary,
            ["SelectionColor"] = selection,
            ["SelectionHoverColor"] = selectionHover,

            // Primary accent & contrast
            ["PrimaryColor"] = primary,
            ["PrimaryColor75"] = WithAlpha(primary, 191),
            ["PrimaryColor50"] = WithAlpha(primary, 128),
            ["PrimaryColor25"] = WithAlpha(primary, 64),
            ["PrimaryColor20"] = WithAlpha(primary, 51),
            ["PrimaryColor10"] = WithAlpha(primary, 26),
            ["PrimaryForegroundColor"] = primaryForeground,
            ["PrimaryHoverColor"] = primaryHover,

            // Secondary & Destructive
            ["SecondaryColor"] = palette.Secondary,
            ["SecondaryColor75"] = WithAlpha(palette.Secondary, 191),
            ["SecondaryColor50"] = WithAlpha(palette.Secondary, 128),
            ["SecondaryColor10"] = WithAlpha(palette.Secondary, 26),
            ["SecondaryForegroundColor"] = palette.Foreground,
            ["DestructiveColor"] = palette.Destructive,
            ["DestructiveColor75"] = WithAlpha(palette.Destructive, 191),
            ["DestructiveColor50"] = WithAlpha(palette.Destructive, 128),
            ["DestructiveColor10"] = WithAlpha(palette.Destructive, 26),
            ["DestructiveForegroundColor"] = destructiveForeground,

            // Semantic status tokens
            ["InfoColor"] = palette.Info,
            ["InfoColor60"] = WithAlpha(palette.Info, 153),
            ["InfoColor20"] = WithAlpha(palette.Info, 51),
            ["InfoColor10"] = WithAlpha(palette.Info, 26),
            ["InfoColor5"] = WithAlpha(palette.Info, 13),
            ["SuccessColor"] = palette.Success,
            ["SuccessColor60"] = WithAlpha(palette.Success, 153),
            ["SuccessColor20"] = WithAlpha(palette.Success, 51),
            ["SuccessColor10"] = WithAlpha(palette.Success, 26),
            ["SuccessColor5"] = WithAlpha(palette.Success, 13),
            ["WarningColor"] = palette.Warning,
            ["WarningColor60"] = WithAlpha(palette.Warning, 153),
            ["WarningColor20"] = WithAlpha(palette.Warning, 51),
            ["WarningColor10"] = WithAlpha(palette.Warning, 26),
            ["WarningColor5"] = WithAlpha(palette.Warning, 13),
            ["ErrorColor"] = error,
            ["ErrorColor60"] = WithAlpha(error, 153),
            ["ErrorColor20"] = WithAlpha(error, 51),
            ["ErrorColor10"] = WithAlpha(error, 26),
            ["ErrorColor5"] = WithAlpha(error, 13),

            // Specific ShadUI control & window surfaces
            ["InputBackgroundColor"] = isLight ? palette.InputBackground : WithAlpha(palette.InputBackground, 196),
            ["BadgeForegroundColor"] = badgeForeground,
            ["BusyAreaOverlayColor"] = WithAlpha(palette.WindowBackground, 180),
            ["NavigationBarBackgroundColor"] = isLight ? WithAlpha(palette.CardBackground, 110) : WithAlpha(palette.Secondary, 48),
            ["SettingsItemBackgroundColor"] = isLight ? WithAlpha(palette.CardBackground, 185) : WithAlpha(palette.CardBackground, 96),
            ["SettingsItemHoverBackgroundColor"] = isLight ? WithAlpha(palette.CardBackground, 215) : WithAlpha(palette.CardBackground, 136),

            ["CardBackgroundColor"] = isLight ? WithAlpha(palette.CardBackground, 175) : WithAlpha(palette.CardBackground, 135),
            ["LightCardBackgroundColor"] = isLight ? WithAlpha(palette.CardBackground, 205) : WithAlpha(palette.Foreground, 14),
            ["SecondaryCardBackgroundColor"] = isLight ? WithAlpha(palette.SecondaryCardBackground, 170) : WithAlpha(palette.SecondaryCardBackground, 175),

            ["DialogOverlayColor"] = Colors.Black,
            ["DialogBackgroundColor"] = palette.DialogBackground,

            ["TitleBarBackgroundColor"] = WithAlpha(sidebar, 115),
            ["PopupBackgroundColor"] = palette.DialogBackground,
            ["ToolTipBackgroundColor"] = palette.DialogBackground,
            ["WindowBackgroundColor"] = WithAlpha(palette.WindowBackground, 224),
            ["WindowButtonHoverColor"] = WithAlpha(palette.Foreground, 18),

            ["SidebarBackgroundColor"] = WithAlpha(sidebar, 115),
            ["SliderThumbBackgroundColor"] = isLight ? palette.CardBackground : palette.Foreground,

            ["SwitchBackgroundColor"] = isLight ? Blend(palette.Border, palette.MutedForeground, 0.25) : WithAlpha(palette.Secondary, 160),
            ["SwitchForegroundColor"] = isLight ? palette.CardBackground : palette.Foreground,

            ["AcrylicWindowBackgroundColor"] = WithAlpha(palette.WindowBackground, 238),
            ["QuoteColor"] = quote,

            // Avalonia & AvaloniaEdit compatibility tokens
            ["ThemeBackgroundColor"] = palette.DialogBackground,
            ["ThemeForegroundColor"] = palette.Foreground,
            ["ThemeBorderLowColor"] = palette.Border,
            ["ThemeControlMidHighColor"] = palette.MutedForeground,
            ["SystemBaseHighColor"] = palette.Foreground,

            // Syntax highlighting & Markdown HTML stylesheet
            ["ColorTheme"] = theme.CodeTheme ?? (isLight ? ThemeName.LightPlus : ThemeName.DarkPlus),
            ["BaseStylesheet"] = BuildMarkdownStylesheet(
                palette.Foreground,
                palette.Border,
                palette.Info,
                palette.MutedForeground,
                quote,
                palette.DialogBackground,
                ghost,
                isLight),
        };

        if (theme.TokenOverrides is { Count: > 0 } overrides)
        {
            foreach (var (key, value) in overrides)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    entries[key] = value;
                }
            }
        }

        target.SetItems(entries);
    }

    private static string BuildMarkdownStylesheet(
        Color foreground,
        Color border,
        Color info,
        Color lead,
        Color quote,
        Color dialogBackground,
        Color ghost,
        bool isLight)
    {
        var blockquoteBackground = isLight
            ? FormatCssHex(dialogBackground)
            : $"rgba({dialogBackground.R}, {dialogBackground.G}, {dialogBackground.B}, 0.45)";

        return $$"""
body {
  font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, "Helvetica Neue", Arial, sans-serif;
  color: {{FormatCssHex(foreground)}};
  font-size: 14px;
  line-height: 1.6;
  margin: 0;
  padding: 0;
}
h1, h2, h3, h4, h5, h6 {
  color: {{FormatCssHex(foreground)}};
  font-weight: 600;
  margin-top: 24px;
  margin-bottom: 16px;
  line-height: 1.25;
}
h1 {
  font-size: 1.8em;
  padding-bottom: 0.3em;
  border-bottom: 1px solid {{FormatCssHex(border)}};
}
h2 {
  font-size: 1.5em;
  padding-bottom: 0.3em;
  border-bottom: 1px solid {{FormatCssHex(border)}};
}
h3 { font-size: 1.25em; }
p, ul, ol {
  margin-top: 0;
  margin-bottom: 16px;
}
a {
  color: {{FormatCssHex(info)}};
  text-decoration: none;
}
a:hover {
  text-decoration: underline;
}
blockquote {
  margin: 0 0 16px 0;
  padding: 8px 16px;
  color: {{FormatCssHex(lead)}};
  border-left: 4px solid {{FormatCssHex(quote)}};
  background-color: {{blockquoteBackground}};
}
code {
  font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", Menlo, monospace;
  font-size: 0.9em;
  color: {{FormatCssHex(foreground)}};
  background-color: {{FormatCssHex(ghost)}};
  padding: 2px 4px;
  border-radius: 4px;
}
pre {
  background-color: {{FormatCssHex(dialogBackground)}};
  border: 1px solid {{FormatCssHex(border)}};
  padding: 12px;
  border-radius: 6px;
  overflow: auto;
}
pre code {
  background-color: transparent;
  padding: 0;
  color: inherit;
}
hr {
  height: 1px;
  border: none;
  background-color: {{FormatCssHex(border)}};
  margin: 24px 0;
}
ul, ol { padding-left: 2em; }
""";
    }

    private static string FormatCssHex(Color color) =>
        string.Create(CultureInfo.InvariantCulture, $"#{color.R:X2}{color.G:X2}{color.B:X2}");

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static Color Blend(Color from, Color to, double ratio) => Color.FromArgb(
        MixByte(from.A, to.A, ratio),
        MixByte(from.R, to.R, ratio),
        MixByte(from.G, to.G, ratio),
        MixByte(from.B, to.B, ratio));

    private static Color ComputeHoverColor(Color accent, Color target) => Color.FromArgb(
        accent.A,
        MixByte(accent.R, target.R, 0.10),
        MixByte(accent.G, target.G, 0.10),
        MixByte(accent.B, target.B, 0.10));

    private static byte MixByte(byte first, byte second, double ratio) =>
        (byte)Math.Clamp(Math.Round(first + (second - first) * ratio), 0, 255);

    private static Color SelectContrastInk(Color background, Color darkInk, Color lightInk, bool preferDark)
    {
        var backgroundLuminance = GetRelativeLuminance(background);
        var darkContrast = GetContrastRatio(backgroundLuminance, GetRelativeLuminance(darkInk));
        var lightContrast = GetContrastRatio(backgroundLuminance, GetRelativeLuminance(lightInk));

        if (preferDark)
        {
            return darkContrast * ForegroundPreference >= lightContrast ? darkInk : lightInk;
        }

        return darkContrast >= lightContrast * ForegroundPreference ? darkInk : lightInk;
    }

    private static double GetRelativeLuminance(Color color) =>
        0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);

    private static double Linearize(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static double GetContrastRatio(double first, double second) =>
        (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
}

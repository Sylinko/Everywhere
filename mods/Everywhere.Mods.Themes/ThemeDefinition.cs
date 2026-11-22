using Avalonia.Media;
using Everywhere.I18N;
using TextMateSharp.Grammars;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Describes a selectable visual theme registered with <see cref="ThemeRegistry"/>.
/// </summary>
public sealed record ThemeDefinition
{
    /// <summary>
    /// Gets the unique stable theme identifier, for example <c>catppuccin.mocha</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the polarity variant this theme targets.
    /// </summary>
    public required ThemeBaseVariant Variant { get; init; }

    /// <summary>
    /// Gets the semantic color palette used to derive runtime tokens. When <see cref="IsHostDefault"/>
    /// is <see langword="true"/>, the palette is still used to render preview swatches while ShadUI's
    /// built-in resource dictionaries remain active.
    /// </summary>
    public required ThemePalette Palette { get; init; }

    /// <summary>
    /// Gets an optional localized display name key.
    /// </summary>
    public IDynamicLocaleKey? DisplayNameKey { get; init; }

    /// <summary>
    /// Gets an optional plain display name used when <see cref="DisplayNameKey"/> is not provided.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Gets an optional theme author attribution.
    /// </summary>
    public string? Author { get; init; }

    /// <summary>
    /// Gets an optional syntax highlighting theme for code blocks and terminals.
    /// </summary>
    public ThemeName? CodeTheme { get; init; }

    /// <summary>
    /// Gets optional exact resource-key overrides applied after standard token expansion.
    /// </summary>
    public IReadOnlyDictionary<string, Color>? TokenOverrides { get; init; }

    /// <summary>
    /// Gets whether this entry represents Everywhere's built-in ShadUI default without overrides.
    /// </summary>
    internal bool IsHostDefault { get; init; }

    /// <summary>
    /// Gets whether this entry was loaded from user-supplied JSON files rather than code/presets.
    /// </summary>
    internal bool IsCustomJson { get; init; }

    /// <summary>
    /// Gets the effective localized display name key for UI presentation.
    /// </summary>
    public IDynamicLocaleKey ResolvedDisplayNameKey =>
        DisplayNameKey ?? new DirectLocaleKey(string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName);
}

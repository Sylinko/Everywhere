using Avalonia.Media;

namespace Everywhere.Mods.Themes;

/// <summary>
/// Defines a clean, orthogonal semantic palette that <see cref="ShadThemeCompiler"/> expands
/// into all ShadUI and Everywhere theme resource tokens.
/// </summary>
public sealed record ThemePalette
{
    /// <summary>
    /// Gets the base window surface color. Translucent variants are derived automatically.
    /// </summary>
    public required Color WindowBackground { get; init; }

    /// <summary>
    /// Gets the primary card surface color.
    /// </summary>
    public required Color CardBackground { get; init; }

    /// <summary>
    /// Gets the secondary or recessed panel surface color, such as code block bodies.
    /// </summary>
    public required Color SecondaryCardBackground { get; init; }

    /// <summary>
    /// Gets the opaque surface color used for dialogs, popups, tooltips, and Markdown pre blocks.
    /// </summary>
    public required Color DialogBackground { get; init; }

    /// <summary>
    /// Gets the surface color for editable inputs.
    /// </summary>
    public required Color InputBackground { get; init; }

    /// <summary>
    /// Gets an optional dedicated sidebar and title bar surface color; defaults to <see cref="WindowBackground"/>.
    /// </summary>
    public Color? SidebarBackground { get; init; }

    /// <summary>
    /// Gets the primary text and icon ink color.
    /// </summary>
    public required Color Foreground { get; init; }

    /// <summary>
    /// Gets the secondary, muted, and lead text color.
    /// </summary>
    public required Color MutedForeground { get; init; }

    /// <summary>
    /// Gets the primary border and hairline divider color.
    /// </summary>
    public required Color Border { get; init; }

    /// <summary>
    /// Gets an optional outline and focus-ring border color; defaults to a blend of <see cref="Border"/> and <see cref="MutedForeground"/>.
    /// </summary>
    public Color? Outline { get; init; }

    /// <summary>
    /// Gets the primary brand accent color.
    /// </summary>
    public required Color Primary { get; init; }

    /// <summary>
    /// Gets the secondary control fill color.
    /// </summary>
    public required Color Secondary { get; init; }

    /// <summary>
    /// Gets the destructive action accent color.
    /// </summary>
    public required Color Destructive { get; init; }

    /// <summary>
    /// Gets the informational status and link color.
    /// </summary>
    public required Color Info { get; init; }

    /// <summary>
    /// Gets the success status color.
    /// </summary>
    public required Color Success { get; init; }

    /// <summary>
    /// Gets the warning status color.
    /// </summary>
    public required Color Warning { get; init; }

    /// <summary>
    /// Gets an optional error status color; defaults to <see cref="Destructive"/>.
    /// </summary>
    public Color? Error { get; init; }

    /// <summary>
    /// Gets an optional blockquote and inline-code accent color; defaults to <see cref="Warning"/>.
    /// </summary>
    public Color? Quote { get; init; }

    /// <summary>
    /// Gets an optional list/menu selection highlight base color.
    /// </summary>
    public Color? Selection { get; init; }
}

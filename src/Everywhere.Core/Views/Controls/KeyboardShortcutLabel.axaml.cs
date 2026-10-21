using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Everywhere.Interop;

namespace Everywhere.Views;

/// <summary>
/// Displays a keyboard shortcut with optional text before it, without participating in focus or keyboard capture.
/// </summary>
public class KeyboardShortcutLabel : TemplatedControl
{
    /// <summary>
    /// Defines the text to display before the shortcut, if any.
    /// </summary>
    public static readonly StyledProperty<string?> TextProperty =
        TextBlock.TextProperty.AddOwner<KeyboardShortcutLabel>();

    /// <summary>
    /// Gets or sets the text to display before the shortcut, if any.
    /// </summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// Defines the shortcut to display, including single keys and modifier-only combinations.
    /// </summary>
    public static readonly StyledProperty<KeyboardShortcut> ShortcutProperty =
        AvaloniaProperty.Register<KeyboardShortcutLabel, KeyboardShortcut>(nameof(Shortcut));

    /// <summary>
    /// Gets or sets the shortcut to display. An empty shortcut hides its presentation.
    /// </summary>
    public KeyboardShortcut Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }
}
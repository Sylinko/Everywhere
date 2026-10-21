using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Metadata;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using Everywhere.Interop;

namespace Everywhere.Views;

/// <summary>
/// Displays a keyboard shortcut as one text flow without participating in focus or keyboard capture.
/// Apply the Compact class for a smaller presentation. Padding and CornerRadius apply to the whole shortcut.
/// </summary>
[TemplatePart(TextBlockPartName, typeof(TextBlock), IsRequired = true)]
public class KeyboardShortcutPresenter : TemplatedControl
{
    /// <summary>
    /// Identifies the text block that hosts the shortcut's inline content.
    /// </summary>
    public const string TextBlockPartName = "PART_TextBlock";

    /// <summary>
    /// Defines the shortcut to display, including single keys and modifier-only combinations.
    /// </summary>
    public static readonly StyledProperty<KeyboardShortcut> ShortcutProperty =
        AvaloniaProperty.Register<KeyboardShortcutPresenter, KeyboardShortcut>(nameof(Shortcut));

    /// <summary>
    /// Gets or sets the shortcut to display. An empty shortcut hides the presentation, including its border.
    /// </summary>
    public KeyboardShortcut Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>
    /// Defines the line height of the shortcut's text flow.
    /// </summary>
    public static readonly StyledProperty<double> LineHeightProperty = TextBlock.LineHeightProperty.AddOwner<KeyboardShortcutPresenter>();

    /// <summary>
    /// Gets or sets the line height of the shortcut's text flow.
    /// </summary>
    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    private static readonly Geometry WindowsIcon = StreamGeometry.Parse("M17 0V17H0V0H17Zm0 38H0V21H17V38ZM21 0H38V17H21V0ZM38 21V38H21V21H38Z");
    private static readonly Geometry CopilotIcon = StreamGeometry.Parse(
        "m564 110c43 0 82 26 98 66l2 6 20 61c4 13 17 22 31 22 30 0 59 7 82 27 22 20 32 46 35 75 6 56-12 132-46 223-15 40-41 84-60 116-23 38-64 60-107 63h-8-223a105 105 0 01-98-66l-2-6-20-61a32 32 0 00-31-22c-30 0-59-7-82-27-22-20-32-46-35-75-6-56 12-132 46-223 15-40 41-84 60-116 23-38 64-60 107-63h8zm153 228h-31c-35 0-66 22-78 54l-2 6-85 283-2 7-3 7h96c22 0 42-11 52-28 19-30 41-70 54-104l9-24a921 921 0 0017-53l5-19c10-40 13-70 11-92-2-17-7-25-12-29-4-4-11-7-25-8zM466 610c-6 1-12 2-19 3h-10-99l20 60a32 32 0 0027 22h4 15c20 0 38-12 46-31l2-5zm-29-427h-96c-22 0-42 11-52 28-19 30-41 70-54 104l-9 24a886 886 0 00-17 53l-5 19c-10 40-13 70-11 92 2 17 7 25 12 29 4 4 11 7 25 8h6 31c35 0 66-22 78-54l2-6 85-283 2-7zm117 155h-38c-29 0-55 19-64 48l-35 116a159 159 0 01-18 39h38c29 0 55-19 64-48l35-116a159 159 0 0118-39m10-155h-15c-20 0-38 12-46 31l-2 5-15 49c6-1 12-2 19-3h10 100l-20-60a32 32 0 00-27-22z");

    private TextBlock? _textBlock;

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _textBlock = e.NameScope.Get<TextBlock>(TextBlockPartName);
        _textBlock.Inlines = new InlineCollection();
        UpdateInlines();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ShortcutProperty)
        {
            UpdateInlines();
        }
    }

    private void UpdateInlines()
    {
        if (_textBlock?.Inlines is not { } inlines)
        {
            return;
        }

        inlines.Clear();
        var shortcut = Shortcut;
        if (shortcut.IsEmpty)
        {
            return;
        }

#if IsOSX
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            AddPart(inlines, new Run("⌘"));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control))
        {
            AddPart(inlines, new Run("⌃"));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            AddPart(inlines, new Run("⌥"));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            AddPart(inlines, new Run("⇧"));
        }
#else
        if (shortcut is { Modifiers: (KeyModifiers.Shift | KeyModifiers.Meta), Key: Key.F23 })
        {
            AddPart(inlines, CreateIcon(CopilotIcon));
            return;
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            AddPart(inlines, CreateIcon(WindowsIcon));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control))
        {
            AddPart(inlines, new Run("Ctrl"));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            AddPart(inlines, new Run("Alt"));
        }

        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            AddPart(inlines, new Run("Shift"));
        }
#endif

        if (shortcut.Key != Key.None)
        {
            var run = new Run();
            run.Bind(Run.TextProperty, GetKeyName(shortcut.Key).ToBinding());
            AddPart(inlines, run);
        }
    }

    private static void AddPart(InlineCollection inlines, Inline part)
    {
#if !IsOSX
        if (inlines.Count > 0)
        {
            inlines.Add(new Run("+"));
        }
#endif

        inlines.Add(part);
    }

    private InlineUIContainer CreateIcon(Geometry geometry)
    {
        var icon = new PathIcon { Data = geometry };
        // Bind to the presenter so Compact and theme changes resize existing icons without
        // rebuilding the text flow. Inline baseline alignment keeps them centered with the text.
        icon.Bind(WidthProperty, new Binding(nameof(FontSize)) { Source = this });
        icon.Bind(HeightProperty, new Binding(nameof(FontSize)) { Source = this });
        icon.Bind(ForegroundProperty, new Binding(nameof(Foreground)) { Source = this });
        return new InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center };
    }

    private static IDynamicLocaleKey GetKeyName(Key key)
    {
        return key switch
        {
            Key.Enter => new DynamicLocaleKey(LocaleKey.Common_KeyboardKey_Enter),
            Key.Space => new DynamicLocaleKey(LocaleKey.Common_KeyboardKey_Space),
            Key.Back => new DynamicLocaleKey(LocaleKey.Common_KeyboardKey_Backspace),
            Key.Delete => new DirectLocaleKey("Del"),
            Key.Escape => new DirectLocaleKey("Esc"),
            >= Key.D0 and <= Key.D9 => new DirectLocaleKey(((int)key - (int)Key.D0).ToString()),
            _ => new DirectLocaleKey(key.ToString())
        };
    }
}
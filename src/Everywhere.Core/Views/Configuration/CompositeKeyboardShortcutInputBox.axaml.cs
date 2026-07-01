using Avalonia.Controls.Primitives;
using Everywhere.Configuration;

namespace Everywhere.Views;

public class CompositeKeyboardShortcutInputBox : TemplatedControl
{
    public static readonly StyledProperty<CompositeKeyboardShortcut?> ShortcutProperty =
        AvaloniaProperty.Register<CompositeKeyboardShortcutInputBox, CompositeKeyboardShortcut?>(nameof(Shortcut));

    public CompositeKeyboardShortcut? Shortcut
    {
        get => GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }
}
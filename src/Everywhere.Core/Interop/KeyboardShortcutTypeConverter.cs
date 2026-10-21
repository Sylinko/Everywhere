using System.ComponentModel;
using System.Globalization;
using Avalonia.Input;

namespace Everywhere.Interop;

/// <summary>
/// Converts AXAML shortcut literals using Avalonia's key-gesture syntax.
/// This does not change the shortcut's persisted representation or registration rules.
/// </summary>
public sealed class KeyboardShortcutTypeConverter : TypeConverter
{
    /// <inheritdoc />
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    /// <inheritdoc />
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text)
        {
            return base.ConvertFrom(context, culture, value);
        }

        var gesture = KeyGesture.Parse(text);
        return new KeyboardShortcut(gesture.Key, gesture.KeyModifiers);
    }
}
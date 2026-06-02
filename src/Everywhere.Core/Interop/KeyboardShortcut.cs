using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using Avalonia.Input;
using ZLinq;

namespace Everywhere.Interop;

/// <summary>
/// Represents a keyboard shortcut consisting of a key and modifier keys.
/// </summary>
/// <param name="Key"></param>
/// <param name="Modifiers"></param>
[TypeConverter(typeof(KeyboardShortcutTypeConverter))]
public readonly record struct KeyboardShortcut(Key Key, KeyModifiers Modifiers)
{
    [JsonIgnore]
    public bool IsEmpty => Key == Key.None && Modifiers == KeyModifiers.None;

    [JsonIgnore]
    public bool IsValid => Key != Key.None && Modifiers != KeyModifiers.None;

    public override string ToString()
    {
#if IsOSX
        const char meta = '⌘';
        const char control = '⌃';
        const char shift = '⇧';
        const char alt = '⌥';
#else
        const string meta = "Win+";
        const string control = "Ctrl+";
        const string shift = "Shift+";
        const string alt = "Alt+";

        if (Modifiers == (KeyModifiers.Shift | KeyModifiers.Meta) && Key == Key.F23)
        {
            return "Copilot";
        }
#endif

        var sb = new StringBuilder();
        if (Modifiers.HasFlag(KeyModifiers.Meta)) sb.Append(meta);
        if (Modifiers.HasFlag(KeyModifiers.Control)) sb.Append(control);
        if (Modifiers.HasFlag(KeyModifiers.Shift)) sb.Append(shift);
        if (Modifiers.HasFlag(KeyModifiers.Alt)) sb.Append(alt);
        if (Key != Key.None) sb.Append(Key);
        return sb.ToString();
    }
}

public sealed class KeyboardShortcutTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string str) return base.ConvertFrom(context, culture, value);

        var modifiers = KeyModifiers.None;
        var key = Key.None;
        foreach (var part in str.Split('+', StringSplitOptions.RemoveEmptyEntries).AsValueEnumerable().Select(p => p.Trim()))
        {
            if (Enum.TryParse<KeyModifiers>(part, true, out var m))
            {
                modifiers |= m;
            }
            else if (Enum.TryParse<Key>(part, true, out var k))
            {
                key = k;
            }
        }

        return new KeyboardShortcut(key, modifiers);
    }
}
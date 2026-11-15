using System.ComponentModel;
using System.Text;
using System.Text.Json.Serialization;
using Avalonia.Input;

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
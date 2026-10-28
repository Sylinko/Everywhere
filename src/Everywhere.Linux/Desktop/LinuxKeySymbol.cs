using System.Globalization;
using Avalonia.Input;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Converts framework keys to the XKB names used by Linux desktop shortcut APIs.
/// </summary>
internal static class LinuxKeySymbol
{
    /// <summary>
    /// Gets the XKB keysym name for a supported framework key.
    /// </summary>
    public static string FromKey(Key key)
    {
        return key switch
        {
            Key.Enter => "Return",
            Key.Escape => "Escape",
            Key.Space => "space",
            Key.Back => "BackSpace",
            Key.Tab => "Tab",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "Prior",
            Key.PageDown => "Next",
            Key.CapsLock => "Caps_Lock",
            Key.NumLock => "Num_Lock",
            Key.Scroll => "Scroll_Lock",
            Key.PrintScreen => "Print",
            Key.Pause => "Pause",
            Key.Apps => "Menu",
            Key.Add => "KP_Add",
            Key.Subtract => "KP_Subtract",
            Key.Multiply => "KP_Multiply",
            Key.Divide => "KP_Divide",
            Key.Decimal => "KP_Decimal",
            Key.OemPlus => "equal",
            Key.OemMinus => "minus",
            Key.OemComma => "comma",
            Key.OemPeriod => "period",
            Key.Oem1 => "semicolon",
            Key.Oem2 => "slash",
            Key.Oem3 => "grave",
            Key.Oem4 => "bracketleft",
            Key.Oem5 => "backslash",
            Key.Oem6 => "bracketright",
            Key.Oem7 => "apostrophe",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            >= Key.A and <= Key.Z => key.ToString(),
            >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(CultureInfo.InvariantCulture),
            >= Key.F1 and <= Key.F24 => key.ToString(),
            >= Key.NumPad0 and <= Key.NumPad9 => "KP_" + ((int)key - (int)Key.NumPad0).ToString(CultureInfo.InvariantCulture),
            _ => throw new NotSupportedException($"The shortcut key {key} has no registered keysym mapping."),
        };
    }
}
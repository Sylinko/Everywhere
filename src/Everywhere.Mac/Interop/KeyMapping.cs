using Avalonia.Input;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides mapping from macOS specific key codes and flags to Avalonia key enums.
/// </summary>
public static class KeyMapping
{
    /// <summary>
    /// Converts CGEventFlags to Avalonia's KeyModifiers.
    /// </summary>
    /// <param name="flags"></param>
    /// <returns></returns>
    public static KeyModifiers ToAvaloniaKeyModifiers(this CGEventFlags flags)
    {
        var modifiers = KeyModifiers.None;
        if (flags.HasFlag(CGEventFlags.Shift))
            modifiers |= KeyModifiers.Shift;
        if (flags.HasFlag(CGEventFlags.Control))
            modifiers |= KeyModifiers.Control;
        if (flags.HasFlag(CGEventFlags.Alternate))
            modifiers |= KeyModifiers.Alt;
        if (flags.HasFlag(CGEventFlags.Command))
            modifiers |= KeyModifiers.Meta;
        return modifiers;
    }

    /// <summary>
    /// Converts Avalonia's KeyModifiers to CGEventFlags.
    /// </summary>
    /// <param name="flags"></param>
    /// <returns></returns>
    public static CGEventFlags ToCGEventFlags(this KeyModifiers flags)
    {
        var modifiers = default(CGEventFlags);
        if (flags.HasFlag(KeyModifiers.Shift))
            modifiers |= CGEventFlags.Shift;
        if (flags.HasFlag(KeyModifiers.Control))
            modifiers |= CGEventFlags.Control;
        if (flags.HasFlag(KeyModifiers.Alt))
            modifiers |= CGEventFlags.Alternate;
        if (flags.HasFlag(KeyModifiers.Meta))
            modifiers |= CGEventFlags.Command;
        return modifiers;
    }

    /// <summary>
    /// Converts NSEventModifierMask to Avalonia's KeyModifiers.
    /// </summary>
    public static KeyModifiers ToAvaloniaKeyModifiers(this NSEventModifierMask flags)
    {
        var modifiers = KeyModifiers.None;
        if (flags.HasFlag(NSEventModifierMask.ShiftKeyMask))
            modifiers |= KeyModifiers.Shift;
        if (flags.HasFlag(NSEventModifierMask.ControlKeyMask))
            modifiers |= KeyModifiers.Control;
        if (flags.HasFlag(NSEventModifierMask.AlternateKeyMask))
            modifiers |= KeyModifiers.Alt;
        if (flags.HasFlag(NSEventModifierMask.CommandKeyMask))
            modifiers |= KeyModifiers.Meta;
        return modifiers;
    }

    /// <summary>
    /// Converts a macOS virtual key code to an Avalonia Key.
    /// </summary>
    public static Key ToAvaloniaKey(this ushort macKeyCode)
    {
        return MacToAvaloniaMap.TryGetValue((CGKeyCode)macKeyCode, out var key) ? key : Key.None;
    }

    // This is a partial mapping. You would need to extend it for full coverage.
    // Key codes can be found in macOS's <Carbon/Events.h> or online resources.
    private static readonly Dictionary<CGKeyCode, Key> MacToAvaloniaMap = new()
    {
        // Letters
        { CGKeyCode.A, Key.A }, { CGKeyCode.B, Key.B }, { CGKeyCode.C, Key.C }, { CGKeyCode.D, Key.D }, { CGKeyCode.E, Key.E },
        { CGKeyCode.F, Key.F }, { CGKeyCode.G, Key.G }, { CGKeyCode.H, Key.H }, { CGKeyCode.I, Key.I }, { CGKeyCode.J, Key.J },
        { CGKeyCode.K, Key.K }, { CGKeyCode.L, Key.L }, { CGKeyCode.M, Key.M }, { CGKeyCode.N, Key.N }, { CGKeyCode.O, Key.O },
        { CGKeyCode.P, Key.P }, { CGKeyCode.Q, Key.Q }, { CGKeyCode.R, Key.R }, { CGKeyCode.S, Key.S }, { CGKeyCode.T, Key.T },
        { CGKeyCode.U, Key.U }, { CGKeyCode.V, Key.V }, { CGKeyCode.W, Key.W }, { CGKeyCode.X, Key.X }, { CGKeyCode.Y, Key.Y },
        { CGKeyCode.Z, Key.Z },

        // Numbers
        { CGKeyCode.D0, Key.D0 }, { CGKeyCode.D1, Key.D1 }, { CGKeyCode.D2, Key.D2 }, { CGKeyCode.D3, Key.D3 }, { CGKeyCode.D4, Key.D4 },
        { CGKeyCode.D5, Key.D5 }, { CGKeyCode.D6, Key.D6 }, { CGKeyCode.D7, Key.D7 }, { CGKeyCode.D8, Key.D8 }, { CGKeyCode.D9, Key.D9 },

        // Numpad
        { CGKeyCode.NumPad0, Key.NumPad0 }, { CGKeyCode.NumPad1, Key.NumPad1 }, { CGKeyCode.NumPad2, Key.NumPad2 },
        { CGKeyCode.NumPad3, Key.NumPad3 }, { CGKeyCode.NumPad4, Key.NumPad4 }, { CGKeyCode.NumPad5, Key.NumPad5 },
        { CGKeyCode.NumPad6, Key.NumPad6 }, { CGKeyCode.NumPad7, Key.NumPad7 },
        { CGKeyCode.NumPad8, Key.NumPad8 }, { CGKeyCode.NumPad9, Key.NumPad9 },
        { CGKeyCode.Add, Key.Add }, { CGKeyCode.Subtract, Key.Subtract }, { CGKeyCode.Multiply, Key.Multiply },
        { CGKeyCode.Divide, Key.Divide }, { CGKeyCode.Decimal, Key.Decimal }, { CGKeyCode.NumPadEnter, Key.Enter },

        // Function keys
        { CGKeyCode.F1, Key.F1 }, { CGKeyCode.F2, Key.F2 }, { CGKeyCode.F3, Key.F3 }, { CGKeyCode.F4, Key.F4 },
        { CGKeyCode.F5, Key.F5 }, { CGKeyCode.F6, Key.F6 }, { CGKeyCode.F7, Key.F7 }, { CGKeyCode.F8, Key.F8 },
        { CGKeyCode.F9, Key.F9 }, { CGKeyCode.F10, Key.F10 }, { CGKeyCode.F11, Key.F11 }, { CGKeyCode.F12, Key.F12 },

        // Special keys
        { CGKeyCode.Escape, Key.Escape }, { CGKeyCode.Space, Key.Space }, { CGKeyCode.Enter, Key.Enter },
        { CGKeyCode.Tab, Key.Tab }, { CGKeyCode.Back, Key.Back }, { CGKeyCode.Delete, Key.Delete },
        { CGKeyCode.Up, Key.Up }, { CGKeyCode.Down, Key.Down }, { CGKeyCode.Left, Key.Left }, { CGKeyCode.Right, Key.Right },
        { CGKeyCode.Home, Key.Home }, { CGKeyCode.End, Key.End }, { CGKeyCode.PageUp, Key.PageUp }, { CGKeyCode.PageDown, Key.PageDown },
    };
}
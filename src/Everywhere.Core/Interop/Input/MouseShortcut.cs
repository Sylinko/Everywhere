using Avalonia.Input;

namespace Everywhere.Interop;

/// <summary>
/// Represents a mouse hotkey with a specific button and delay.
/// </summary>
/// <param name="Key"></param>
/// <param name="Delay"></param>
public readonly record struct MouseShortcut(MouseButton Key, TimeSpan Delay);
using Avalonia.Input;
using Everywhere.Interop;

namespace Everywhere.Chat;

/// <summary>
/// Selects which key combination submits a message in the chat input area.
/// </summary>
public enum ChatSendKeyMode
{
    /// <summary>
    /// Press Enter to submit and Shift+Enter to insert a newline.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatSendKeyMode_Enter)]
    Enter = 0,

    /// <summary>
    /// Press Ctrl+Enter (or Cmd+Enter on macOS) to submit and Enter to insert a newline.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatSendKeyMode_CtrlEnter)]
    CtrlEnter = 1
}

/// <summary>
/// Maps <see cref="ChatSendKeyMode"/> values to the corresponding submit, newline, and opposite-mode shortcuts.
/// </summary>
public static class ChatSendKeyModeExtensions
{
    private static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// Gets the primary shortcut that submits a message for <paramref name="mode"/>.
    /// </summary>
    public static KeyboardShortcut GetSendShortcut(this ChatSendKeyMode mode) => mode switch
    {
        ChatSendKeyMode.CtrlEnter => new KeyboardShortcut(Key.Enter, PrimaryModifier),
        _ => new KeyboardShortcut(Key.Enter, KeyModifiers.None)
    };

    /// <summary>
    /// Gets the shortcut that inserts a newline for <paramref name="mode"/>.
    /// </summary>
    public static KeyboardShortcut GetNewLineShortcut(this ChatSendKeyMode mode) => mode switch
    {
        ChatSendKeyMode.CtrlEnter => new KeyboardShortcut(Key.Enter, KeyModifiers.None),
        _ => new KeyboardShortcut(Key.Enter, KeyModifiers.Shift)
    };

    /// <summary>
    /// Gets the shortcut that submits using the opposite busy delivery mode for <paramref name="mode"/>.
    /// </summary>
    public static KeyboardShortcut GetOppositeModeShortcut(this ChatSendKeyMode mode) => mode switch
    {
        ChatSendKeyMode.CtrlEnter => new KeyboardShortcut(Key.Enter, PrimaryModifier | KeyModifiers.Shift),
        _ => new KeyboardShortcut(Key.Enter, PrimaryModifier)
    };
}

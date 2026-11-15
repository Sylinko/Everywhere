namespace Everywhere.Interop;

/// <summary>
/// Provides a Global keyboard and mouse shortcut listener.
/// </summary>
public interface IShortcutListener
{
    // Register a keyboard shortcut. Multiple handlers for the same shortcut are supported.
    // Returns an IDisposable that unregisters this handler only.
    IDisposable Register(KeyboardShortcut shortcut, Action handler);

    // Register a mouse shortcut. Multiple handlers for the same MouseKey (with different delays) are supported.
    // Returns an IDisposable that unregisters this handler only.
    IDisposable Register(MouseShortcut shortcut, Action handler);

    /// <summary>
    /// Starts capturing the keyboard shortcut
    /// </summary>
    /// <returns></returns>
    IKeyboardShortcutScope StartCaptureKeyboardShortcut();
}

/// <summary>
/// Represents a scope for capturing keyboard shortcuts. Useful for allowing users to set custom keyboard shortcuts.
/// </summary>
public interface IKeyboardShortcutScope : IDisposable
{
    /// <summary>
    /// Raised when the shortcut is changed during capturing.
    /// e.g., when the user is pressing ctrl+alt+K, this event will be raised when ctrl is pressed, then alt is pressed, then K is pressed.
    /// </summary>
    delegate void PressingShortcutChangedHandler(IKeyboardShortcutScope scope, KeyboardShortcut currentShortcut);

    delegate void ShortcutFinishedHandler(IKeyboardShortcutScope scope, KeyboardShortcut finalShortcut);

    KeyboardShortcut PressingShortcut { get; }

    bool IsDisposed { get; }

    event PressingShortcutChangedHandler PressingShortcutChanged;

    event ShortcutFinishedHandler ShortcutFinished;
}
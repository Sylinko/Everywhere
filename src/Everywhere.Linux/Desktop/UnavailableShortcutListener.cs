using Everywhere.Interop;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Rejects unported global shortcuts locally, without starting the Host desired-state recovery loop.
/// </summary>
public sealed class UnavailableShortcutListener : IShortcutListener
{
    /// <inheritdoc />
    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        throw new NotSupportedException("Global keyboard shortcuts have not been ported to this desktop backend.");
    }

    /// <inheritdoc />
    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        throw new NotSupportedException("Global mouse shortcuts have not been ported to this desktop backend.");
    }

    /// <inheritdoc />
    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        throw new NotSupportedException("Global shortcut capture has not been ported to this desktop backend.");
    }
}
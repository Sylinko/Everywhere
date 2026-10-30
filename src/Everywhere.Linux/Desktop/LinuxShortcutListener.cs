using Avalonia.Threading;
using Everywhere.Interop;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Records shortcuts from focused application windows and delegates global registrations
/// to the desktop backend when one is available. Recording is independent of the window manager.
/// </summary>
public sealed partial class LinuxShortcutListener : IShortcutListener
{
    private readonly IShortcutListener? _globalShortcuts;
    private KeyboardShortcutScope? _captureScope;

    /// <summary>
    /// Creates a listener with an optional global backend. During focused recording,
    /// the backend's capture scope suspends its owned bindings without supplying key events.
    /// </summary>
    /// <param name="globalShortcuts">
    /// The available global listener, or null for desktops that only support focused recording.
    /// </param>
    public LinuxShortcutListener(IShortcutListener? globalShortcuts)
    {
        _globalShortcuts = globalShortcuts;
    }

    /// <inheritdoc />
    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (_globalShortcuts is null)
        {
            throw new NotSupportedException("Global keyboard shortcuts have not been ported to this desktop backend.");
        }

        return _globalShortcuts.Register(shortcut, handler);
    }

    /// <inheritdoc />
    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        if (_globalShortcuts is null)
        {
            throw new NotSupportedException("Global mouse shortcuts have not been ported to this desktop backend.");
        }

        return _globalShortcuts.Register(shortcut, handler);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Recording and scope disposal belong to the UI thread. The scope observes application
    /// windows rather than the currently focused element because the editor acquires focus
    /// after this method returns. Its owner releases the scope when that focus is lost.
    /// </remarks>
    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        Dispatcher.UIThread.VerifyAccess();

        if (_captureScope is not null)
        {
            return _captureScope;
        }

        var globalCapture = _globalShortcuts?.StartCaptureKeyboardShortcut();
        _captureScope = new KeyboardShortcutScope(this, globalCapture);
        return _captureScope;
    }

    private void ReleaseCapture(KeyboardShortcutScope scope)
    {
        if (ReferenceEquals(_captureScope, scope))
        {
            _captureScope = null;
        }
    }
}
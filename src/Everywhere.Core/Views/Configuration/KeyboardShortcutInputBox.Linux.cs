#if LINUX
using Avalonia.Input;
using Everywhere.Interop;

namespace Everywhere.Views;

public partial class KeyboardShortcutInputBox
{
    private KeyboardShortcut _pendingShortcut;

    /// <inheritdoc />
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (!IsRecording || _shortcutScope is not { } scope)
        {
            base.OnKeyDown(e);
            return;
        }

        // The Input Host capture lease pauses owned bindings but does not publish
        // layout-aware raw keys. Record events from this focused Wayland surface.
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _pendingShortcut = default;
            HandleShortcutScopeShortcutFinished(scope, default);
            return;
        }

        var modifier = ModifierForKey(e.Key);
        var modifiers = e.KeyModifiers | modifier;
        _pendingShortcut = new KeyboardShortcut(modifier == KeyModifiers.None ? e.Key : _pendingShortcut.Key, modifiers);
        HandleShortcutScopePressingShortcutChanged(scope, _pendingShortcut);
    }

    /// <inheritdoc />
    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (!IsRecording || _shortcutScope is not { } scope)
        {
            _pendingShortcut = default;
            base.OnKeyUp(e);
            return;
        }

        e.Handled = true;
        if (e.Key == _pendingShortcut.Key)
        {
            var completed = _pendingShortcut;
            _pendingShortcut = default;
            if (completed.IsValid)
            {
                HandleShortcutScopeShortcutFinished(scope, completed);
                return;
            }
        }

        // Keep the accepted chord's modifiers if they are released before its key.
        // Only the unfinished display follows the currently held modifier set.
        HandleShortcutScopePressingShortcutChanged(
            scope,
            new KeyboardShortcut(_pendingShortcut.Key, e.KeyModifiers & ~ModifierForKey(e.Key)));
    }

    private static KeyModifiers ModifierForKey(Key key)
    {
        return key switch
        {
            Key.LeftCtrl or Key.RightCtrl => KeyModifiers.Control,
            Key.LeftAlt or Key.RightAlt => KeyModifiers.Alt,
            Key.LeftShift or Key.RightShift => KeyModifiers.Shift,
            Key.LWin or Key.RWin => KeyModifiers.Meta,
            _ => KeyModifiers.None,
        };
    }
}
#endif
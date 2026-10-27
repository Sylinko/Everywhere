using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Everywhere.Interop;
using ShadUI.Extensions;

namespace Everywhere.Linux.Desktop;

public sealed partial class LinuxShortcutListener
{
    /// <summary>
    /// Owns focused key observation and the optional global-binding suspension lease.
    /// All local state is UI-thread-affine; remote cancellation is marshalled at its boundary.
    /// </summary>
    private sealed class KeyboardShortcutScope : IKeyboardShortcutScope
    {
        /// <inheritdoc />
        public KeyboardShortcut PressingShortcut { get; private set; }

        /// <inheritdoc />
        public bool IsDisposed { get; private set; }

        /// <inheritdoc />
        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        /// <inheritdoc />
        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        private readonly LinuxShortcutListener _owner;
        private readonly IKeyboardShortcutScope? _globalCapture;
        private readonly IDisposable _keyDownSubscription;
        private readonly IDisposable _keyUpSubscription;
        private KeyboardShortcut _pendingShortcut;

        public KeyboardShortcutScope(LinuxShortcutListener owner, IKeyboardShortcutScope? globalCapture)
        {
            _owner = owner;
            _globalCapture = globalCapture;
            _keyDownSubscription = InputElement.KeyDownEvent.AddClassHandler<TopLevel>(
                HandleKeyDown,
                RoutingStrategies.Tunnel);
            _keyUpSubscription = InputElement.KeyUpEvent.AddClassHandler<TopLevel>(
                HandleKeyUp,
                RoutingStrategies.Tunnel);

            if (_globalCapture is not null)
            {
                _globalCapture.ShortcutFinished += HandleGlobalCaptureFinished;
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispatcher.UIThread.VerifyAccess();

            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _keyDownSubscription.Dispose();
            _keyUpSubscription.Dispose();
            _owner.ReleaseCapture(this);

            if (_globalCapture is not null)
            {
                _globalCapture.ShortcutFinished -= HandleGlobalCaptureFinished;
                _globalCapture.Dispose();
            }
        }

        private void HandleKeyDown(TopLevel sender, KeyEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            e.Handled = true;
            if (e.Key == Key.Escape)
            {
                Complete(default);
                return;
            }

            var modifier = ModifierForKey(e.Key);
            var modifiers = e.KeyModifiers | modifier;
            var key = modifier == KeyModifiers.None ? e.Key : _pendingShortcut.Key;
            _pendingShortcut = new KeyboardShortcut(key, modifiers);
            PublishChanged(_pendingShortcut);
        }

        private void HandleKeyUp(TopLevel sender, KeyEventArgs e)
        {
            if (IsDisposed)
            {
                return;
            }

            e.Handled = true;
            if (e.Key == _pendingShortcut.Key && _pendingShortcut.IsValid)
            {
                Complete(_pendingShortcut);
                return;
            }

            // Releasing modifiers updates the preview without changing the chord accepted
            // on key-down. It must survive modifiers being released before the main key.
            PublishChanged(new KeyboardShortcut(_pendingShortcut.Key, e.KeyModifiers & ~ModifierForKey(e.Key)));
        }

        private void PublishChanged(KeyboardShortcut shortcut)
        {
            PressingShortcut = shortcut;
            PressingShortcutChanged?.Invoke(this, shortcut);
        }

        private void Complete(KeyboardShortcut shortcut)
        {
            if (IsDisposed)
            {
                return;
            }

            PressingShortcut = shortcut;
            Dispose();
            ShortcutFinished?.Invoke(this, shortcut);
        }

        private void HandleGlobalCaptureFinished(IKeyboardShortcutScope scope, KeyboardShortcut shortcut)
        {
            // The Host owns binding suspension, not local recording. Losing that lease
            // cancels recording instead of leaving the UI with a stale capture scope.
            Dispatcher.UIThread.PostOnDemand(() => Complete(default));
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
}
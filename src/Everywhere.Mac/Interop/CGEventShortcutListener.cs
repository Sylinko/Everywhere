using System.Collections.Immutable;
using System.Reactive.Disposables;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Utilities;
using Serilog;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides a global keyboard and mouse shortcut listener for macOS using CoreGraphics Event Taps.
/// Requires Accessibility permissions.
/// </summary>
// ReSharper disable once InconsistentNaming
public sealed class CGEventShortcutListener : IShortcutListener, IDisposable
{
    private readonly Dictionary<KeyboardShortcut, KeyboardRegistration> _keyboardRegistrations = new();
    private readonly Dictionary<MouseShortcut, List<Action>> _mouseHandlers = new();
    private readonly Lock _syncLock = new();

    private KeyboardShortcutScopeImpl? _currentCaptureScope;
    private KeyModifiers _swallowedModifiers = KeyModifiers.None;

    public CGEventShortcutListener()
    {
        CGEventListener.Default.EventReceived += HandleEvent;
    }

    private void HandleEvent(CGEventType type, CGEvent cgEvent, ref nint cgEventRef)
    {
        switch (type)
        {
            case CGEventType.KeyDown:
                HandleKeyDown(cgEvent, ref cgEventRef);
                break;
            case CGEventType.KeyUp:
                HandleKeyUp(ref cgEventRef);
                break;
            case CGEventType.FlagsChanged:
                HandleFlagsChanged(cgEvent, ref cgEventRef);
                break;
            case CGEventType.LeftMouseDown:
            case CGEventType.LeftMouseUp:
            case CGEventType.RightMouseDown:
            case CGEventType.RightMouseUp:
            case CGEventType.OtherMouseDown:
            case CGEventType.OtherMouseUp:
                // HandleMouse(type, nsEvent);
                break;
        }
    }

    private void HandleKeyDown(CGEvent cgEvent, ref nint cgEventRef)
    {
        var key = ((ushort)cgEvent.GetLongValueField(CGEventField.KeyboardEventKeycode)).ToAvaloniaKey();
        var modifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();
        var shortcut = new KeyboardShortcut(key, modifiers);

        KeyboardRegistration? registration;
        using (var _ = _syncLock.EnterScope())
        {
            if (_currentCaptureScope is not null)
            {
                if (key == Key.Escape)
                {
                    _currentCaptureScope.CancelAndNotify();
                }
                else
                {
                    _currentCaptureScope.UpdateKey(key);
                }

                cgEventRef = 0; // Swallow the event
                return;
            }

            _keyboardRegistrations.TryGetValue(shortcut, out registration);
        }

        if (registration is { Count: > 0 })
        {
            ThreadPool.QueueUserWorkItem(_ => registration.SafeExecute());

            cgEventRef = 0; // Swallow the actual KeyDown event

            // Record which modifiers were part of this swallowed hotkey,
            // so we can swallow their corresponding FlagsChanged (KeyUp equivalents).
            if (modifiers != KeyModifiers.None)
            {
                _swallowedModifiers = modifiers;
            }
        }
    }

    private void HandleKeyUp(ref nint cgEventRef)
    {
        // If we are in capture mode, notify that the shortcut has been finished.
        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope is null) return;

        _currentCaptureScope.NotifyShortcutFinished();
        cgEventRef = 0; // Swallow the event
    }

    private void HandleFlagsChanged(CGEvent cgEvent, ref nint cgEventRef)
    {
        var currentModifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();

        if (_swallowedModifiers != KeyModifiers.None)
        {
            // If the user has completely released all modifiers that were part of the shortcut
            if ((currentModifiers & _swallowedModifiers) == KeyModifiers.None)
            {
                _swallowedModifiers = KeyModifiers.None; // Reset state
            }
            cgEventRef = 0; // Swallow the modifier change
        }

        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope is null) return;

        var previousModifiers = _currentCaptureScope.PressingShortcut.Modifiers;
        _currentCaptureScope.UpdateModifiers(currentModifiers);
        cgEventRef = 0; // Swallow the event

        if (previousModifiers != KeyModifiers.None && currentModifiers == KeyModifiers.None)
        {
            // All modifiers released. If there was no key pressed, or just modifiers, finish capture.
            if (_currentCaptureScope.PressingShortcut.Modifiers == KeyModifiers.None)
            {
                _currentCaptureScope.FinishAndNotify();
            }
        }
    }

    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (shortcut.Key == Key.None || shortcut.Modifiers == KeyModifiers.None)
        {
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));
        }

        ArgumentNullException.ThrowIfNull(handler);

        using var _ = _syncLock.EnterScope();
        if (!_keyboardRegistrations.TryGetValue(shortcut, out var registration))
        {
            registration = new KeyboardRegistration(shortcut);
            _keyboardRegistrations[shortcut] = registration;
        }

        registration.Add(handler);

        return Disposable.Create(() =>
        {
            using var _ = _syncLock.EnterScope();
            if (_keyboardRegistrations.TryGetValue(shortcut, out var existingRegistration))
            {
                existingRegistration.Remove(handler);
                if (existingRegistration.Count == 0)
                {
                    _keyboardRegistrations.Remove(shortcut);
                }
            }
        });
    }

    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        // TODO: Implement mouse shortcut registration.
        // This will involve listening to mouse events in HandleEvent,
        // managing timers for delays, and invoking handlers.
        throw new NotImplementedException();
    }

    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        using var _ = _syncLock.EnterScope();
        if (_currentCaptureScope != null) return _currentCaptureScope;

        // Start a new capture scope
        var scope = new KeyboardShortcutScopeImpl(this);
        _currentCaptureScope = scope;
        return scope;
    }

    public void Dispose()
    {
        DisposeHelper.DisposeToDefault(ref _currentCaptureScope);
        CGEventListener.Default.EventReceived -= HandleEvent;
        _keyboardRegistrations.Clear();
    }

    private sealed class KeyboardRegistration(KeyboardShortcut shortcut)
    {
        private ImmutableArray<Action> _handlers = ImmutableArray<Action>.Empty;

        public int Count => _handlers.Length;

        public void Add(Action h) => ImmutableInterlocked.Update(ref _handlers, x => x.Add(h));
        public void Remove(Action h) => ImmutableInterlocked.Update(ref _handlers, x => x.Remove(h));

        public void SafeExecute()
        {
            var handlersSnapshot = _handlers;
            foreach (var handler in handlersSnapshot)
            {
                try { handler(); }
                catch (Exception ex)
                {
                    Log.ForContext<CGEventShortcutListener>().Error(
                        ex,
                        "Exception occurred while handling macOS keyboard shortcut {Shortcut}.",
                        shortcut);
                }
            }
        }
    }

    /// <summary>
    /// Implementation of IKeyboardShortcutScope for capturing keyboard shortcuts.
    /// This class is intended to be used internally by CGEventShortcutListener.
    /// </summary>
    private sealed class KeyboardShortcutScopeImpl(CGEventShortcutListener owner) : IKeyboardShortcutScope
    {
        public KeyboardShortcut PressingShortcut { get; private set; }

        public bool IsDisposed { get; private set; }

        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        public void NotifyShortcutFinished() => ThreadPool.QueueUserWorkItem(_ => ShortcutFinished?.Invoke(this, PressingShortcut));

        public void UpdateKey(Key key)
        {
            PressingShortcut = PressingShortcut with { Key = key };
            NotifyChanged();
        }

        public void UpdateModifiers(KeyModifiers modifiers)
        {
            PressingShortcut = PressingShortcut with { Modifiers = modifiers };
            NotifyChanged();
        }

        private void NotifyChanged()
        {
            var capturedState = PressingShortcut;
            ThreadPool.QueueUserWorkItem(_ => PressingShortcutChanged?.Invoke(this, capturedState));
        }

        public void FinishAndNotify()
        {
            var finalState = PressingShortcut;
            PressingShortcut = default; // Reset state before firing event
            ThreadPool.QueueUserWorkItem(_ => ShortcutFinished?.Invoke(this, finalState));
        }

        public void CancelAndNotify()
        {
            PressingShortcut = default;
            NotifyChanged();
            FinishAndNotify();
        }

        public void Dispose()
        {
            if (IsDisposed) return;

            using var _ = owner._syncLock.EnterScope();
            if (owner._currentCaptureScope == this) owner._currentCaptureScope = null;
            IsDisposed = true;
        }
    }
}
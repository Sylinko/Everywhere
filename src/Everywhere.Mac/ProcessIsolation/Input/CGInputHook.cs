using Avalonia.Input;
using Everywhere.Mac.Interop;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.Utilities;

namespace Everywhere.Mac.ProcessIsolation.Input;

/// <summary>
/// Owns native keyboard and mouse state for one macOS Input Host connection.
/// Native callbacks only perform bounded state work and attempt a non-blocking
/// write through the owning session.
/// </summary>
public sealed class CGInputHook : IDisposable
{
    /// <summary>Events that can be accepted by the session's bounded queue.</summary>
    public enum InputEventKind
    {
        ShortcutTriggered,
        CaptureChanged,
        CaptureFinished
    }

    /// <summary>Small serializable event produced by the native hook.</summary>
    public readonly record struct InputEvent(
        InputEventKind Kind,
        ulong Id,
        int Key = 0,
        int Modifiers = 0);

    private AtomicBoolean IsDisposed => new(ref _isDisposed);

    private readonly Lock _gate = new();
    private readonly CGEventListener _eventListener;
    private readonly Dictionary<NativeKeyboardShortcut, ulong> _keyboardRegistrationIdsByShortcut = [];
    private readonly Dictionary<ulong, KeyboardRegistration> _keyboardRegistrations = [];
    private readonly Dictionary<MouseButton, List<MouseRegistration>> _mouseRegistrationsByButton = new()
    {
        [MouseButton.Left] = [],
        [MouseButton.Right] = [],
        [MouseButton.Middle] = [],
        [MouseButton.XButton1] = [],
        [MouseButton.XButton2] = []
    };
    private readonly Dictionary<ulong, MouseRegistration> _mouseRegistrations = [];
    private readonly Func<InputEvent, bool> _publish;

    private ulong _captureId;
    private bool _captureCompleted;
    private Key _captureKey;
    private KeyModifiers _captureModifiers;
    private int _isDisposed;
    private KeyModifiers _swallowedModifiers;

    /// <summary>Creates a hook and subscribes it to the default modifying event tap.</summary>
    public CGInputHook(Func<InputEvent, bool> publish)
    {
        _publish = publish;
        if (!AccessibilityPermission.IsTrusted(prompt: false))
        {
            throw new InvalidOperationException("macOS Accessibility permission is not granted.");
        }

        _eventListener = CGEventListener.Default;
        _eventListener.EventReceived += HandleEvent;
    }

    /// <summary>
    /// Reconciles native matching state with one complete Main-owned snapshot.
    /// Existing registrations retain their managed identity when their values match.
    /// </summary>
    public void ApplyState(ApplyInputStateRequest request)
    {
        lock (_gate)
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(CGInputHook));
            }

            ValidateState(request);
            ReconcileKeyboardRegistrationsLocked(request.KeyboardRegistrations);
            ReconcileMouseRegistrationsLocked(request.MouseRegistrations);
            ApplyCaptureLocked(request.CaptureId);
        }
    }

    /// <summary>Detaches the event handler and cancels every connection-owned timer.</summary>
    public void Dispose()
    {
        if (!IsDisposed.FlipIfFalse())
        {
            return;
        }

        _eventListener.EventReceived -= HandleEvent;

        MouseRegistration[] mouseRegistrations;
        lock (_gate)
        {
            mouseRegistrations = [.. _mouseRegistrations.Values];
            _keyboardRegistrations.Clear();
            _keyboardRegistrationIdsByShortcut.Clear();
            _mouseRegistrations.Clear();
            foreach (var registrations in _mouseRegistrationsByButton.Values)
            {
                registrations.Clear();
            }

            _captureId = 0;
            _captureCompleted = false;
            _captureKey = Key.None;
            _captureModifiers = KeyModifiers.None;
            _swallowedModifiers = KeyModifiers.None;
        }

        foreach (var registration in mouseRegistrations)
        {
            registration.Dispose();
        }
    }

    private static void ValidateState(ApplyInputStateRequest request)
    {
        var registrationIds = new HashSet<ulong>();
        var keyboardShortcuts = new HashSet<NativeKeyboardShortcut>();
        foreach (var registration in request.KeyboardRegistrations)
        {
            if (registration.RegistrationId == 0 || !registrationIds.Add(registration.RegistrationId))
            {
                throw new InvalidOperationException("The macOS Input Host received duplicate or empty keyboard registration identity.");
            }

            if (!keyboardShortcuts.Add(
                    new NativeKeyboardShortcut((Key)registration.Key, (KeyModifiers)registration.Modifiers)))
            {
                throw new InvalidOperationException("The macOS Input Host received duplicate keyboard shortcut state.");
            }
        }

        foreach (var registration in request.MouseRegistrations)
        {
            if (registration.RegistrationId == 0 || !registrationIds.Add(registration.RegistrationId))
            {
                throw new InvalidOperationException("The macOS Input Host received duplicate or empty mouse registration identity.");
            }

            if (!IsSupportedMouseButton((MouseButton)registration.Button))
            {
                throw new InvalidOperationException("The macOS Input Host received an unsupported mouse button.");
            }
        }
    }

    private void ReconcileKeyboardRegistrationsLocked(InputKeyboardRegistration[] registrations)
    {
        var desired = registrations.ToDictionary(static registration => registration.RegistrationId);
        foreach (var current in _keyboardRegistrations.Values.ToArray())
        {
            if (!desired.TryGetValue(current.RegistrationId, out var registration) || !current.Matches(registration))
            {
                RemoveKeyboardRegistrationLocked(current);
            }
        }

        foreach (var registration in registrations)
        {
            if (!_keyboardRegistrations.ContainsKey(registration.RegistrationId))
            {
                AddKeyboardRegistrationLocked(registration);
            }
        }
    }

    private void AddKeyboardRegistrationLocked(InputKeyboardRegistration registration)
    {
        var shortcut = new NativeKeyboardShortcut((Key)registration.Key, (KeyModifiers)registration.Modifiers);
        _keyboardRegistrations.Add(
            registration.RegistrationId,
            new KeyboardRegistration(registration.RegistrationId, shortcut));
        _keyboardRegistrationIdsByShortcut.Add(shortcut, registration.RegistrationId);
    }

    private void RemoveKeyboardRegistrationLocked(KeyboardRegistration registration)
    {
        _keyboardRegistrations.Remove(registration.RegistrationId);
        _keyboardRegistrationIdsByShortcut.Remove(registration.Shortcut);
    }

    private void ReconcileMouseRegistrationsLocked(InputMouseRegistration[] registrations)
    {
        var desired = registrations.ToDictionary(static registration => registration.RegistrationId);
        foreach (var current in _mouseRegistrations.Values.ToArray())
        {
            if (!desired.TryGetValue(current.RegistrationId, out var registration) || !current.Matches(registration))
            {
                RemoveMouseRegistrationLocked(current);
            }
        }

        foreach (var registration in registrations)
        {
            if (!_mouseRegistrations.ContainsKey(registration.RegistrationId))
            {
                AddMouseRegistrationLocked(registration);
            }
        }
    }

    private void AddMouseRegistrationLocked(InputMouseRegistration registration)
    {
        var button = (MouseButton)registration.Button;
        var mouseRegistration = new MouseRegistration(
            registration.RegistrationId,
            button,
            registration.DelayTicks,
            () => _publish(new InputEvent(InputEventKind.ShortcutTriggered, registration.RegistrationId)));
        _mouseRegistrations.Add(registration.RegistrationId, mouseRegistration);
        _mouseRegistrationsByButton[button].Add(mouseRegistration);
    }

    private void RemoveMouseRegistrationLocked(MouseRegistration registration)
    {
        _mouseRegistrations.Remove(registration.RegistrationId);
        _mouseRegistrationsByButton[registration.Button].Remove(registration);
        registration.Dispose();
    }

    private void ApplyCaptureLocked(ulong captureId)
    {
        if (_captureId == captureId)
        {
            return;
        }

        _captureId = captureId;
        _captureCompleted = false;
        _captureKey = Key.None;
        _captureModifiers = KeyModifiers.None;
        _swallowedModifiers = KeyModifiers.None;
    }

    private void HandleEvent(CGEventType type, CGEvent cgEvent, ref nint cgEventRef)
    {
        var originalEventRef = cgEventRef;
        try
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
                    HandleMouse(type, cgEvent);
                    break;
            }
        }
        catch
        {
            // A native callback must never turn a managed failure into an event-tap
            // failure. Returning the original event is the fail-open behavior.
            cgEventRef = originalEventRef;
        }
    }

    private void HandleKeyDown(CGEvent cgEvent, ref nint cgEventRef)
    {
        var key = ((ushort)cgEvent.GetLongValueField(CGEventField.KeyboardEventKeycode)).ToAvaloniaKey();
        var modifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();

        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            if (_captureId != 0)
            {
                if (_captureCompleted)
                {
                    return;
                }

                if (key == Key.Escape)
                {
                    if (PublishCaptureLocked(InputEventKind.CaptureFinished, Key.None, KeyModifiers.None))
                    {
                        cgEventRef = 0;
                    }

                    return;
                }

                if (PublishCaptureLocked(InputEventKind.CaptureChanged, key, modifiers))
                {
                    cgEventRef = 0;
                }

                return;
            }

            if (!_keyboardRegistrationIdsByShortcut.TryGetValue(new NativeKeyboardShortcut(key, modifiers), out var registrationId))
            {
                return;
            }

            if (!_publish(new InputEvent(InputEventKind.ShortcutTriggered, registrationId)))
            {
                return;
            }

            cgEventRef = 0;
            // Only an accepted shortcut may swallow the matching modifier release.
            _swallowedModifiers = modifiers;
        }
    }

    private void HandleKeyUp(ref nint cgEventRef)
    {
        lock (_gate)
        {
            if (IsDisposed || _captureId == 0 || _captureCompleted ||
                (_captureKey == Key.None && _captureModifiers == KeyModifiers.None))
            {
                return;
            }

            if (PublishCaptureLocked(InputEventKind.CaptureFinished, _captureKey, _captureModifiers))
            {
                cgEventRef = 0;
            }
        }
    }

    private void HandleFlagsChanged(CGEvent cgEvent, ref nint cgEventRef)
    {
        var modifiers = cgEvent.Flags.ToAvaloniaKeyModifiers();
        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            if (_captureId != 0)
            {
                if (_captureCompleted)
                {
                    return;
                }

                var kind = modifiers == KeyModifiers.None && _captureModifiers != KeyModifiers.None
                    ? InputEventKind.CaptureFinished
                    : InputEventKind.CaptureChanged;
                if (PublishCaptureLocked(kind, _captureKey, modifiers))
                {
                    cgEventRef = 0;
                }

                return;
            }

            if (_swallowedModifiers == KeyModifiers.None)
            {
                return;
            }

            cgEventRef = 0;
            if ((modifiers & _swallowedModifiers) == KeyModifiers.None)
            {
                _swallowedModifiers = KeyModifiers.None;
            }
        }
    }

    private bool PublishCaptureLocked(InputEventKind kind, Key key, KeyModifiers modifiers)
    {
        var accepted = _publish(new InputEvent(kind, _captureId, (int)key, (int)modifiers));
        if (!accepted)
        {
            return false;
        }

        _captureKey = key;
        _captureModifiers = modifiers;
        if (kind == InputEventKind.CaptureFinished)
        {
            _captureCompleted = true;
        }

        return true;
    }

    private void HandleMouse(CGEventType type, CGEvent cgEvent)
    {
        if (TryGetMouseButton(type, cgEvent) is not { } button)
        {
            return;
        }

        MouseRegistration[] registrations;
        lock (_gate)
        {
            if (IsDisposed || !_mouseRegistrationsByButton.TryGetValue(button, out var registered) || registered.Count == 0)
            {
                return;
            }

            registrations = [.. registered];
        }

        if (IsMouseDown(type))
        {
            foreach (var registration in registrations)
            {
                registration.OnDown();
            }
        }
        else
        {
            foreach (var registration in registrations)
            {
                registration.OnUp();
            }
        }
    }

    private static MouseButton? TryGetMouseButton(CGEventType type, CGEvent cgEvent) => type switch
    {
        CGEventType.LeftMouseDown or CGEventType.LeftMouseUp => MouseButton.Left,
        CGEventType.RightMouseDown or CGEventType.RightMouseUp => MouseButton.Right,
        CGEventType.OtherMouseDown or CGEventType.OtherMouseUp => cgEvent.GetLongValueField(CGEventField.MouseEventButtonNumber) switch
        {
            2 => MouseButton.Middle,
            3 => MouseButton.XButton1,
            4 => MouseButton.XButton2,
            _ => null
        },
        _ => null
    };

    private static bool IsMouseDown(CGEventType type) => type is
        CGEventType.LeftMouseDown or
        CGEventType.RightMouseDown or
        CGEventType.OtherMouseDown;

    private static bool IsSupportedMouseButton(MouseButton button) => button is
        MouseButton.Left or
        MouseButton.Right or
        MouseButton.Middle or
        MouseButton.XButton1 or
        MouseButton.XButton2;

    private readonly record struct NativeKeyboardShortcut(Key Key, KeyModifiers Modifiers);

    private sealed class KeyboardRegistration(ulong registrationId, NativeKeyboardShortcut shortcut)
    {
        public ulong RegistrationId { get; } = registrationId;

        public NativeKeyboardShortcut Shortcut { get; } = shortcut;

        public bool Matches(InputKeyboardRegistration registration) =>
            Shortcut.Key == (Key)registration.Key && Shortcut.Modifiers == (KeyModifiers)registration.Modifiers;
    }

    private sealed class MouseRegistration(
        ulong registrationId,
        MouseButton button,
        long delayTicks,
        Func<bool> publish) : IDisposable
    {
        private readonly Lock _gate = new();
        private readonly TimeSpan _delay = TimeSpan.FromTicks(delayTicks);

        private PendingTimer? _timer;
        private bool _isDisposed;

        public ulong RegistrationId { get; } = registrationId;

        public MouseButton Button { get; } = button;

        public void OnDown()
        {
            Timer? previousTimer;
            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                if (_delay <= TimeSpan.Zero)
                {
                    publish();
                    return;
                }

                var pendingTimer = new PendingTimer(this);
                pendingTimer.Timer = new Timer(
                    static state =>
                    {
                        if (state is PendingTimer pending)
                        {
                            pending.Owner.OnTimer(pending);
                        }
                    },
                    pendingTimer,
                    _delay,
                    Timeout.InfiniteTimeSpan);
                previousTimer = _timer?.Timer;
                _timer = pendingTimer;
            }

            previousTimer?.Dispose();
        }

        public void OnUp()
        {
            Timer? timer;
            lock (_gate)
            {
                timer = _timer?.Timer;
                _timer = null;
            }

            timer?.Dispose();
        }

        public bool Matches(InputMouseRegistration registration) =>
            Button == (MouseButton)registration.Button && _delay.Ticks == registration.DelayTicks;

        public void Dispose()
        {
            Timer? timer;
            lock (_gate)
            {
                if (_isDisposed)
                {
                    return;
                }

                _isDisposed = true;
                timer = _timer?.Timer;
                _timer = null;
            }

            timer?.Dispose();
        }

        private void OnTimer(PendingTimer pendingTimer)
        {
            Timer? timer;
            lock (_gate)
            {
                if (_isDisposed || !ReferenceEquals(_timer, pendingTimer))
                {
                    return;
                }

                _timer = null;
                timer = pendingTimer.Timer;
                publish();
            }

            timer?.Dispose();
        }

        private sealed class PendingTimer(MouseRegistration owner)
        {
            public MouseRegistration Owner { get; } = owner;

            public Timer? Timer { get; set; }
        }
    }
}
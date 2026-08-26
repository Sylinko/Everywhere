using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Input;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.Utilities;
using Everywhere.Windows.Extensions;
using Everywhere.Windows.Interop;

namespace Everywhere.Windows.ProcessIsolation.Input;

/// <summary>
/// Owns all native keyboard and mouse resources for one Input Host connection.
/// Its callback returns whether a low-level keyboard event was accepted for delivery,
/// which lets the hook fail open when the bounded session queue is full.
/// </summary>
public sealed unsafe class Win32InputHook : IDisposable
{
    public enum InputEventKind
    {
        ShortcutTriggered,
        CaptureChanged,
        CaptureFinished
    }

    public readonly record struct InputEvent(
        InputEventKind Kind,
        ulong Id,
        int Key = 0,
        int Modifiers = 0
    );

    private const nuint InjectExtra = 0x0d000721;

    private static HWND HWnd => MessageWindow.Shared.HWnd;

    private AtomicBoolean IsDisposed => new(ref _isDisposed);

    private readonly IHostDiagnosticsRpc _diagnostics;
    private readonly Dictionary<NativeKeyboardShortcut, ulong> _fallbackRegistrations = [];
    private readonly Lock _gate = new();
    private readonly Dictionary<ulong, KeyboardRegistration> _keyboardRegistrations = [];
    private readonly Dictionary<int, ulong> _nativeHotKeyRegistrations = [];
    private readonly IDisposable _messageSubscription;
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

    private bool _captureCompleted;
    private ulong _captureId;
    private Key _captureKey;
    private KeyModifiers _captureModifiers;
    private KeyModifiers _pressedModifiers;
    private int _isDisposed;
    private IDisposable? _keyboardHook;
    private IDisposable? _mouseHook;
    private int _nextHotKeyId;

    public Win32InputHook(Func<InputEvent, bool> publish, IHostDiagnosticsRpc diagnostics)
    {
        _publish = publish;
        _diagnostics = diagnostics;
        _messageSubscription = MessageWindow.Shared.AddHandler((uint)WINDOW_MESSAGE.WM_HOTKEY, HandleHotKeyMessage);
    }

    /// <summary>
    /// Reconciles native resources with one complete Main-owned snapshot. Unchanged
    /// registrations retain their OS identity, so replay and duplicate snapshots are cheap.
    /// </summary>
    public void ApplyState(ApplyInputStateRequest request)
    {
        IDisposable? keyboardHookToDispose;
        IDisposable? mouseHookToDispose;
        lock (_gate)
        {
            ReconcileKeyboardRegistrationsLocked(request.KeyboardRegistrations);
            ReconcileMouseRegistrationsLocked(request.MouseRegistrations);
            ApplyCaptureLocked(request.CaptureId);

            keyboardHookToDispose = TakeUnusedKeyboardHookLocked();
            mouseHookToDispose = TakeUnusedMouseHookLocked();
        }

        try
        {
            keyboardHookToDispose?.Dispose();
        }
        finally
        {
            mouseHookToDispose?.Dispose();
        }
    }

    public void Dispose()
    {
        if (!IsDisposed.FlipIfFalse())
        {
            return;
        }

        int[] nativeHotKeyIds;
        MouseRegistration[] mouseRegistrations;
        IDisposable? keyboardHook;
        IDisposable? mouseHook;
        lock (_gate)
        {
            nativeHotKeyIds = [.. _nativeHotKeyRegistrations.Keys];
            mouseRegistrations = [.. _mouseRegistrations.Values];
            keyboardHook = _keyboardHook;
            mouseHook = _mouseHook;

            _captureId = 0;
            _keyboardHook = null;
            _mouseHook = null;
            _fallbackRegistrations.Clear();
            _keyboardRegistrations.Clear();
            _mouseRegistrations.Clear();
            foreach (var registrations in _mouseRegistrationsByButton.Values)
            {
                registrations.Clear();
            }
            _nativeHotKeyRegistrations.Clear();
        }

        _messageSubscription.Dispose();
        foreach (var nativeHotKeyId in nativeHotKeyIds)
        {
            PInvoke.UnregisterHotKey(HWnd, nativeHotKeyId);
        }

        foreach (var registration in mouseRegistrations)
        {
            registration.Dispose();
        }

        try
        {
            keyboardHook?.Dispose();
        }
        finally
        {
            mouseHook?.Dispose();
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
        var modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Control))
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift))
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt))
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
        }
        if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta))
        {
            modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;
        }

        var nativeHotKeyId = ++_nextHotKeyId;
        if (PInvoke.RegisterHotKey(HWnd, nativeHotKeyId, modifiers, (uint)shortcut.Key.ToVirtualKey()))
        {
            _nativeHotKeyRegistrations.Add(nativeHotKeyId, registration.RegistrationId);
        }
        else
        {
            _diagnostics.LogAsync(
                    new HostLogNotification
                    {
                        Level = HostLogLevel.Warning,
                        Source = nameof(Win32InputHook),
                        Message = $"RegisterHotKey failed; using the low-level keyboard hook. Error: {Marshal.GetLastWin32Error()}."
                    })
                .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            nativeHotKeyId = 0;
            _fallbackRegistrations.Add(shortcut, registration.RegistrationId);
            EnsureKeyboardHookLocked();
        }

        _keyboardRegistrations.Add(
            registration.RegistrationId,
            new KeyboardRegistration(registration.RegistrationId, shortcut, nativeHotKeyId));
    }

    private void RemoveKeyboardRegistrationLocked(KeyboardRegistration registration)
    {
        _keyboardRegistrations.Remove(registration.RegistrationId);
        if (registration.NativeHotKeyId > 0)
        {
            _nativeHotKeyRegistrations.Remove(registration.NativeHotKeyId);
            PInvoke.UnregisterHotKey(HWnd, registration.NativeHotKeyId);
            return;
        }

        _fallbackRegistrations.Remove(registration.Shortcut);
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
        var mouseRegistration = new MouseRegistration(
            registration,
            () => _publish(new InputEvent(InputEventKind.ShortcutTriggered, registration.RegistrationId)));
        _mouseRegistrations.Add(registration.RegistrationId, mouseRegistration);
        _mouseRegistrationsByButton[mouseRegistration.Button].Add(mouseRegistration);
        _mouseHook ??= LowLevelHook.CreateMouseHook(MouseHookProc);
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
        _captureKey = Key.None;
        _captureModifiers = KeyModifiers.None;
        _pressedModifiers = KeyModifiers.None;
        _captureCompleted = false;
        if (captureId != 0)
        {
            EnsureKeyboardHookLocked();
        }
    }

    private void EnsureKeyboardHookLocked() =>
        _keyboardHook ??= LowLevelHook.CreateKeyboardHook(KeyboardHookProc);

    private IDisposable? TakeUnusedKeyboardHookLocked()
    {
        if (_captureId != 0 || _fallbackRegistrations.Count != 0)
        {
            return null;
        }

        var hook = _keyboardHook;
        _keyboardHook = null;
        return hook;
    }

    private IDisposable? TakeUnusedMouseHookLocked()
    {
        if (_mouseRegistrations.Count != 0)
        {
            return null;
        }

        var hook = _mouseHook;
        _mouseHook = null;
        return hook;
    }

    private void HandleHotKeyMessage(in MSG message)
    {
        ulong registrationId;
        lock (_gate)
        {
            if (IsDisposed || !_nativeHotKeyRegistrations.TryGetValue((int)message.wParam.Value, out registrationId))
            {
                return;
            }
        }

        // RegisterHotKey has already consumed the key combination. Queue saturation
        // can drop this notification but cannot retroactively pass the key through.
        _publish(new InputEvent(InputEventKind.ShortcutTriggered, registrationId));
    }

    private void KeyboardHookProc(WINDOW_MESSAGE message, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        if (hookStruct.dwExtraInfo == InjectExtra)
        {
            return;
        }

        bool sendDummyKeyUp;
        lock (_gate)
        {
            if (IsDisposed)
            {
                return;
            }

            if (_captureId != 0 && !_captureCompleted)
            {
                blockNext = HandleCaptureLocked(message, (VIRTUAL_KEY)hookStruct.vkCode);
                return;
            }

            if (message is not (WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN))
            {
                return;
            }

            var shortcut = new NativeKeyboardShortcut(((VIRTUAL_KEY)hookStruct.vkCode).ToAvaloniaKey(), GetAsyncModifiers());
            if (!_fallbackRegistrations.TryGetValue(shortcut, out var registrationId))
            {
                return;
            }

            blockNext = _publish(new InputEvent(InputEventKind.ShortcutTriggered, registrationId));
            sendDummyKeyUp = blockNext;
        }

        if (sendDummyKeyUp)
        {
            SendDummyKeyUp();
        }
    }

    private bool HandleCaptureLocked(WINDOW_MESSAGE message, VIRTUAL_KEY virtualKey)
    {
        if (virtualKey == VIRTUAL_KEY.VK_ESCAPE &&
            message is WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN)
        {
            _captureKey = Key.None;
            _captureModifiers = KeyModifiers.None;
            return PublishCaptureLocked(InputEventKind.CaptureFinished);
        }

        var keyModifiers = virtualKey.ToKeyModifiers();
        switch (message)
        {
            case WINDOW_MESSAGE.WM_KEYDOWN:
            case WINDOW_MESSAGE.WM_SYSKEYDOWN:
                if (keyModifiers == KeyModifiers.None)
                {
                    _captureKey = virtualKey.ToAvaloniaKey();
                }
                else
                {
                    _pressedModifiers |= keyModifiers;
                    _captureModifiers = _pressedModifiers;
                }

                return PublishCaptureLocked(InputEventKind.CaptureChanged);

            case WINDOW_MESSAGE.WM_KEYUP:
            case WINDOW_MESSAGE.WM_SYSKEYUP:
                _pressedModifiers &= ~keyModifiers;
                if (_pressedModifiers != KeyModifiers.None)
                {
                    return _publish(new InputEvent(InputEventKind.CaptureChanged, _captureId, (int)_captureKey, (int)_captureModifiers));
                }

                if (_captureModifiers == KeyModifiers.None || _captureKey == Key.None)
                {
                    _captureKey = Key.None;
                    _captureModifiers = KeyModifiers.None;
                }

                return PublishCaptureLocked(InputEventKind.CaptureFinished);

            default:
                return false;
        }
    }

    private bool PublishCaptureLocked(InputEventKind kind)
    {
        var accepted = _publish(new InputEvent(kind, _captureId, (int)_captureKey, (int)_captureModifiers));
        if (accepted && kind == InputEventKind.CaptureFinished)
        {
            _captureCompleted = true;
        }

        return accepted;
    }

    private void MouseHookProc(WINDOW_MESSAGE message, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        if (hookStruct.dwExtraInfo == InjectExtra || TryGetMouseButton(message, hookStruct.mouseData) is not { } button)
        {
            return;
        }

        MouseRegistration[] registrations;
        lock (_gate)
        {
            if (IsDisposed || _mouseRegistrationsByButton[button].Count == 0)
            {
                return;
            }

            registrations = [.. _mouseRegistrationsByButton[button]];
        }

        var isButtonDown = message is
            WINDOW_MESSAGE.WM_LBUTTONDOWN or
            WINDOW_MESSAGE.WM_RBUTTONDOWN or
            WINDOW_MESSAGE.WM_MBUTTONDOWN or
            WINDOW_MESSAGE.WM_XBUTTONDOWN;
        foreach (var registration in registrations)
        {
            if (isButtonDown)
            {
                registration.OnDown();
            }
            else
            {
                registration.OnUp();
            }
        }
    }

    private static MouseButton? TryGetMouseButton(WINDOW_MESSAGE message, uint mouseData) => message switch
    {
        WINDOW_MESSAGE.WM_LBUTTONDOWN or WINDOW_MESSAGE.WM_LBUTTONUP => MouseButton.Left,
        WINDOW_MESSAGE.WM_RBUTTONDOWN or WINDOW_MESSAGE.WM_RBUTTONUP => MouseButton.Right,
        WINDOW_MESSAGE.WM_MBUTTONDOWN or WINDOW_MESSAGE.WM_MBUTTONUP => MouseButton.Middle,
        WINDOW_MESSAGE.WM_XBUTTONDOWN or WINDOW_MESSAGE.WM_XBUTTONUP when ((mouseData >> 16) & 0xFFFF) == PInvoke.XBUTTON1 => MouseButton.XButton1,
        WINDOW_MESSAGE.WM_XBUTTONDOWN or WINDOW_MESSAGE.WM_XBUTTONUP when ((mouseData >> 16) & 0xFFFF) == PInvoke.XBUTTON2 => MouseButton.XButton2,
        _ => null
    };

    private static KeyModifiers GetAsyncModifiers()
    {
        static bool IsDown(VIRTUAL_KEY key) => (PInvoke.GetAsyncKeyState((int)key) & 0x8000) != 0;

        var modifiers = KeyModifiers.None;
        if (IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN))
        {
            modifiers |= KeyModifiers.Meta;
        }
        if (IsDown(VIRTUAL_KEY.VK_CONTROL))
        {
            modifiers |= KeyModifiers.Control;
        }
        if (IsDown(VIRTUAL_KEY.VK_SHIFT))
        {
            modifiers |= KeyModifiers.Shift;
        }
        if (IsDown(VIRTUAL_KEY.VK_MENU))
        {
            modifiers |= KeyModifiers.Alt;
        }

        return modifiers;
    }

    /// <summary>Injects a harmless key-up so blocking a Win-key shortcut does not open Start.</summary>
    private static void SendDummyKeyUp()
    {
        var input = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)0xFF,
                    dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                    dwExtraInfo = InjectExtra
                }
            }
        };

        PInvoke.SendInput(new ReadOnlySpan<INPUT>(&input, 1), sizeof(INPUT));
    }

    private readonly record struct NativeKeyboardShortcut(Key Key, KeyModifiers Modifiers);

    private sealed class KeyboardRegistration(ulong registrationId, NativeKeyboardShortcut shortcut, int nativeHotKeyId)
    {
        public ulong RegistrationId { get; } = registrationId;

        public NativeKeyboardShortcut Shortcut { get; } = shortcut;

        public int NativeHotKeyId { get; } = nativeHotKeyId;

        public bool Matches(InputKeyboardRegistration registration) =>
            Shortcut.Key == (Key)registration.Key && Shortcut.Modifiers == (KeyModifiers)registration.Modifiers;
    }

    private sealed class MouseRegistration(InputMouseRegistration registration, Func<bool> publish) : IDisposable
    {
        private AtomicBoolean IsDisposed => new(ref _isDisposed);

        private readonly TimeSpan _delay = TimeSpan.FromTicks(registration.DelayTicks);

        private int _armed;
        private int _isDisposed;
        private Timer? _timer;

        public ulong RegistrationId { get; } = registration.RegistrationId;

        public MouseButton Button { get; } = (MouseButton)registration.Button;

        public void OnDown()
        {
            if (IsDisposed)
            {
                return;
            }

            if (_delay <= TimeSpan.Zero)
            {
                publish();
                return;
            }

            Interlocked.Exchange(ref _armed, 1);
            Interlocked.Exchange(
                ref _timer,
                new Timer(
                    _ =>
                    {
                        if (!IsDisposed && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
                        {
                            publish();
                        }
                    },
                    null,
                    _delay,
                    Timeout.InfiniteTimeSpan))?.Dispose();
        }

        public void OnUp()
        {
            Interlocked.Exchange(ref _armed, 0);
            Interlocked.Exchange(ref _timer, null)?.Dispose();
        }

        public bool Matches(InputMouseRegistration registration) =>
            Button == (MouseButton)registration.Button && _delay.Ticks == registration.DelayTicks;

        public void Dispose()
        {
            if (!IsDisposed.FlipIfFalse())
            {
                return;
            }

            OnUp();
        }
    }
}
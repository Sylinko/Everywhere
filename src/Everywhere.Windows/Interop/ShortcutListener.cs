using System.Collections.Immutable;
using System.Reactive.Disposables;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Windows.Extensions;
using Serilog;
using ZLinq;

namespace Everywhere.Windows.Interop;

public unsafe sealed class ShortcutListener : IShortcutListener, IDisposable
{
    /// <summary>
    /// Indicates input events injected by this code to prevent self-interception in hooks.
    /// Arbitrary value with high bits set to avoid conflicts with typical apps.
    /// </summary>
    private const nuint InjectExtra = 0x0d000721;

    private static HWND HWnd => MessageWindow.Shared.HWnd;

    /// <summary>
    /// Global id of RegisterHotKey registrations
    /// </summary>
    private static int _nextId = 1;

    private static readonly Lock SyncLock = new();

    private static IKeyboardShortcutScope? _currentKeyboardShortcutScope;

    private readonly Dictionary<KeyboardShortcut, KeyboardRegistration> _keyboardRegistrations = new();
    private readonly Dictionary<int, KeyboardRegistration> _keyboardRegistrationsById = new(); // Subset of above with Id > 0 (OS-registered) for quick lookup in WM_HOTKEY handler
    private readonly Dictionary<MouseButton, List<MouseRegistration>> _mouseRegistrations = new()
    {
        { MouseButton.Left, [] },
        { MouseButton.Right, [] },
        { MouseButton.Middle, [] },
        { MouseButton.XButton1, [] },
        { MouseButton.XButton2, [] },
    };

    // Hooks (created on demand)
    private IDisposable? _keyboardHookSubscription;
    private IDisposable? _mouseHookSubscription;

    public ShortcutListener()
    {
        MessageWindow.Shared.AddHandler(
            (uint)WINDOW_MESSAGE.WM_HOTKEY,
            (in msg) =>
            {
                var id = (int)msg.wParam.Value;
                KeyboardRegistration? registration;
                lock (SyncLock) _keyboardRegistrationsById.TryGetValue(id, out registration);
                if (registration is not { Id: > 0, Count: > 0 }) return;

                ThreadPool.QueueUserWorkItem(_ => registration.SafeExecute());
            });
    }

    public IKeyboardShortcutScope StartCaptureKeyboardShortcut()
    {
        lock (SyncLock)
        {
            if (_currentKeyboardShortcutScope is not { IsDisposed: false }) _currentKeyboardShortcutScope = new KeyboardShortcutScopeImpl();
            return _currentKeyboardShortcutScope;
        }
    }

    public IDisposable Register(KeyboardShortcut shortcut, Action handler)
    {
        if (shortcut.Key == Key.None || shortcut.Modifiers == KeyModifiers.None)
        {
            throw new ArgumentException("Invalid keyboard shortcut.", nameof(shortcut));
        }

        ArgumentNullException.ThrowIfNull(handler);

        lock (SyncLock)
        {
            if (_keyboardRegistrations.TryGetValue(shortcut, out var existingRegistration))
            {
                existingRegistration.Add(handler);
                return Disposable.Create(() => UnregisterKeyboardHandlerHook(shortcut, handler));
            }

            // Try OS registration (incl. MOD_WIN).
            var modifiers = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= HOT_KEY_MODIFIERS.MOD_CONTROL;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HOT_KEY_MODIFIERS.MOD_SHIFT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HOT_KEY_MODIFIERS.MOD_ALT;
            if (shortcut.Modifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HOT_KEY_MODIFIERS.MOD_WIN;

            var id = _nextId++;
            if (!PInvoke.RegisterHotKey(HWnd, id, modifiers, (uint)shortcut.Key.ToVirtualKey()))
            {
                // If RegisterHotKey failed, set id to 0, which means this registration will be handled by the LL keyboard hook.
                Log.ForContext<ShortcutListener>().Warning(
                    "RegisterHotKey failed for {Shortcut} with modifiers {Modifiers}. Error: {Error}. Falling back to keyboard hook.",
                    shortcut.Key,
                    shortcut.Modifiers,
                    Marshal.GetLastWin32Error());

                id = 0;
            }

            var registration = new KeyboardRegistration(id, handler);
            _keyboardRegistrations[shortcut] = registration;

            if (id > 0)
            {
                _keyboardRegistrationsById[id] = registration;
            }
            else
            {
                _keyboardHookSubscription ??= LowLevelHook.CreateKeyboardHook(KeyboardHookProc);
            }

            return Disposable.Create(() => UnregisterKeyboardHandlerHook(shortcut, handler));
        }
    }

    public IDisposable Register(MouseShortcut shortcut, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (SyncLock)
        {
            var reg = new MouseRegistration(shortcut, handler);
            _mouseRegistrations[shortcut.Key].Add(reg);
            _mouseHookSubscription ??= LowLevelHook.CreateMouseHook(MouseHookProc);
            return Disposable.Create(() => UnregisterMouseHandler(reg));
        }
    }

    public void Dispose()
    {
        lock (SyncLock)
        {
            foreach (var registration in _keyboardRegistrations.Values) registration.Dispose();
            _keyboardRegistrations.Clear();
            _keyboardRegistrationsById.Clear();

            foreach (var list in _mouseRegistrations.Values)
            {
                foreach (var r in list) r.CancelTimer();
                list.Clear();
            }

            DisposeHelper.DisposeToDefault(ref _keyboardHookSubscription);
            DisposeHelper.DisposeToDefault(ref _mouseHookSubscription);
        }
    }

    // ---------- keyboard hook (fallback) ----------
    // Reference: PowerToys
    // https://github.com/microsoft/PowerToys/blob/main/src/modules/cmdpal/CmdPalKeyboardService/KeyboardListener.cpp

    private void KeyboardHookProc(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        // Ignore self-injected events
        if (hookStruct.dwExtraInfo == InjectExtra) return;

        if (msg != WINDOW_MESSAGE.WM_KEYDOWN && msg != WINDOW_MESSAGE.WM_SYSKEYDOWN)
            return;

        var vk = (VIRTUAL_KEY)hookStruct.vkCode;
        var key = vk.ToAvaloniaKey();
        var modifiers = GetAsyncModifiers();
        var shortcut = new KeyboardShortcut(key, modifiers);

        KeyboardRegistration? registration;
        lock (SyncLock) _keyboardRegistrations.TryGetValue(shortcut, out registration);

        // Id > 0 means it should be handled by RegisterHotKey, so only handle if Id == 0 (fallback) and there are handlers to execute.
        if (registration is not { Id: 0, Count: > 0 })
            return;

        ThreadPool.QueueUserWorkItem(_ => registration.SafeExecute());

        // Send a dummy key-up to prevent Start menu from activating on Win-key release
        SendDummyKeyUp();

        // Swallow this key press
        blockNext = true;
    }

    private static KeyModifiers GetAsyncModifiers()
    {
        // Note: Using async state to query left/right Win, Ctrl, Shift, Alt.
        // This mirrors the PowerToys approach and avoids maintaining custom state.
        static bool IsDown(VIRTUAL_KEY v) => (PInvoke.GetAsyncKeyState((int)v) & 0x8000) != 0;

        var modifiers = KeyModifiers.None;

        if (IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN))
            modifiers |= KeyModifiers.Meta;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0)
            modifiers |= KeyModifiers.Control;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_SHIFT) & 0x8000) != 0)
            modifiers |= KeyModifiers.Shift;
        if ((PInvoke.GetAsyncKeyState((int)VIRTUAL_KEY.VK_MENU) & 0x8000) != 0)
            modifiers |= KeyModifiers.Alt;

        return modifiers;
    }

    /// <summary>
    /// Inject a harmless key-up (VK 0xFF) to cancel Start-menu activation
    /// </summary>
    private static void SendDummyKeyUp()
    {
        var inputs = new INPUT[1];
        inputs[0] = new INPUT
        {
            type = INPUT_TYPE.INPUT_KEYBOARD,
            Anonymous = new INPUT._Anonymous_e__Union
            {
                ki = new KEYBDINPUT
                {
                    wVk = (VIRTUAL_KEY)0xFF,
                    wScan = 0,
                    dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = InjectExtra
                }
            }
        };

        fixed (INPUT* p = inputs)
        {
            PInvoke.SendInput(new ReadOnlySpan<INPUT>(p, 1), sizeof(INPUT));
        }
    }

    private void MouseHookProc(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        if (hookStruct.dwExtraInfo == InjectExtra) return;

        var button = (hookStruct.mouseData >> 16) & 0xFFFF;
        switch (msg)
        {
            case WINDOW_MESSAGE.WM_LBUTTONDOWN:
            case WINDOW_MESSAGE.WM_RBUTTONDOWN:
            case WINDOW_MESSAGE.WM_MBUTTONDOWN:
            case WINDOW_MESSAGE.WM_XBUTTONDOWN:
            {
                if (GetRegistrations() is not { Count: > 0 } registrations) break;

                // Schedule or fire per registration
                foreach (var r in registrations) r.OnDown();
                break;
            }

            case WINDOW_MESSAGE.WM_LBUTTONUP:
            case WINDOW_MESSAGE.WM_RBUTTONUP:
            case WINDOW_MESSAGE.WM_MBUTTONUP:
            case WINDOW_MESSAGE.WM_XBUTTONUP:
            {
                if (GetRegistrations() is not { Count: > 0 } registrations) break;

                // Cancel pending per registration
                foreach (var r in registrations) r.OnUp();
                break;
            }
        }

        List<MouseRegistration>? GetRegistrations()
        {
            var mk = msg switch
            {
                WINDOW_MESSAGE.WM_LBUTTONUP => MouseButton.Left,
                WINDOW_MESSAGE.WM_RBUTTONUP => MouseButton.Right,
                WINDOW_MESSAGE.WM_MBUTTONUP => MouseButton.Middle,
                WINDOW_MESSAGE.WM_XBUTTONUP when button == PInvoke.XBUTTON1 => MouseButton.XButton1,
                WINDOW_MESSAGE.WM_XBUTTONUP when button == PInvoke.XBUTTON2 => MouseButton.XButton2,
                _ => MouseButton.None
            };
            if (mk == MouseButton.None) return null;

            lock (SyncLock) return _mouseRegistrations[mk].Count > 0 ? [.._mouseRegistrations[mk]] : null;
        }
    }

    // ---------- unregister helpers ----------

    private void UnregisterKeyboardHandlerHook(KeyboardShortcut shortcut, Action handler)
    {
        lock (SyncLock)
        {
            if (!_keyboardRegistrations.TryGetValue(shortcut, out var registration)) return;

            registration.Remove(handler);
            if (registration.Count == 0)
            {
                _keyboardRegistrations.Remove(shortcut);
                if (registration.Id > 0) _keyboardRegistrationsById.Remove(registration.Id);
                registration.Dispose();
            }

            if (_keyboardRegistrations.Count == 0)
            {
                DisposeHelper.DisposeToDefault(ref _keyboardHookSubscription);
            }
        }
    }

    private void UnregisterMouseHandler(MouseRegistration reg)
    {
        lock (SyncLock)
        {
            if (_mouseRegistrations.TryGetValue(reg.Shortcut.Key, out var list))
            {
                list.Remove(reg);
                reg.CancelTimer();
            }

            if (_mouseRegistrations.Values.AsValueEnumerable().Sum(l => l.Count) == 0 && _mouseHookSubscription is not null)
            {
                _mouseHookSubscription.Dispose();
                _mouseHookSubscription = null;
            }
        }
    }

    private sealed class KeyboardRegistration(int id, Action initialHandler) : IDisposable
    {
        public int Id { get; } = id;
        private ImmutableArray<Action> _handlers = [initialHandler];

        public void Add(Action h) => ImmutableInterlocked.Update(ref _handlers, x => x.Add(h));
        public void Remove(Action h) => ImmutableInterlocked.Update(ref _handlers, x => x.Remove(h));
        public int Count => _handlers.Length;

        /// <summary>
        /// Ensures a snapshot of handlers to allow safe enumeration outside the lock, while allowing modifications to the original list.
        /// </summary>
        public void SafeExecute()
        {
            var handlersSnapshot = _handlers;
            foreach (var handler in handlersSnapshot)
            {
                try { handler(); }
                catch (Exception ex)
                {
                    // Ignore
                    Log.ForContext<ShortcutListener>().Error(ex, "Exception in keyboard shortcut handler");
                }
            }
        }

        public void Dispose()
        {
            _handlers = ImmutableArray<Action>.Empty;
            if (Id > 0) PInvoke.UnregisterHotKey(HWnd, Id);
        }
    }

    // Per-registration mouse state (timer lifecycle)
    private sealed class MouseRegistration(MouseShortcut shortcut, Action handler)
    {
        public MouseShortcut Shortcut { get; } = shortcut;
        public Action Handler { get; } = handler;
        private Timer? _timer;
        private int _armed; // 1 when button is considered pressed/pending

        public void OnDown()
        {
            // mark pressed
            Interlocked.Exchange(ref _armed, 1);

            if (Shortcut.Delay <= TimeSpan.Zero)
            {
                SafeInvoke();
                return;
            }

            CancelTimer(); // defensive
            _timer = new Timer(
                _ =>
                {
                    if (Interlocked.CompareExchange(ref _armed, 1, 1) == 1)
                    {
                        SafeInvoke();
                    }
                },
                null,
                Shortcut.Delay,
                Timeout.InfiniteTimeSpan);
        }

        public void OnUp()
        {
            Interlocked.Exchange(ref _armed, 0);
            CancelTimer();
        }

        public void CancelTimer()
        {
            var t = Interlocked.Exchange(ref _timer, null);
            t?.Dispose();
        }

        private void SafeInvoke()
        {
            try { Handler(); }
            catch
            { /* swallow */
            }
        }
    }

    private sealed class KeyboardShortcutScopeImpl : IKeyboardShortcutScope
    {
        public KeyboardShortcut PressingShortcut { get; private set; }

        public bool IsDisposed { get; private set; }

        public event IKeyboardShortcutScope.PressingShortcutChangedHandler? PressingShortcutChanged;

        public event IKeyboardShortcutScope.ShortcutFinishedHandler? ShortcutFinished;

        private readonly IDisposable _hookSubscription;

        private KeyModifiers _pressedKeyModifiers = KeyModifiers.None;

        public KeyboardShortcutScopeImpl()
        {
            _hookSubscription = LowLevelHook.CreateKeyboardHook(KeyboardHookCallback);
        }

        private void KeyboardHookCallback(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
        {
            var virtualKey = (VIRTUAL_KEY)hookStruct.vkCode;
            if (virtualKey == VIRTUAL_KEY.VK_ESCAPE && msg is WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN)
            {
                // Short circuit on ESC down to allow users to cancel the shortcut capturing by pressing ESC.
                PressingShortcut = default;
                PressingShortcutChanged?.Invoke(this, PressingShortcut);
                ShortcutFinished?.Invoke(this, PressingShortcut);

                blockNext = true;
                return;
            }

            var keyModifiers = virtualKey.ToKeyModifiers();
            bool? isKeyDown = msg switch
            {
                WINDOW_MESSAGE.WM_KEYDOWN => true,
                WINDOW_MESSAGE.WM_SYSKEYDOWN => true,
                WINDOW_MESSAGE.WM_KEYUP => false,
                WINDOW_MESSAGE.WM_SYSKEYUP => false,
                _ => null
            };

            switch (isKeyDown)
            {
                case true when keyModifiers == KeyModifiers.None:
                {
                    PressingShortcut = PressingShortcut with { Key = virtualKey.ToAvaloniaKey() };
                    PressingShortcutChanged?.Invoke(this, PressingShortcut);
                    break;
                }
                case true:
                {
                    _pressedKeyModifiers |= keyModifiers;
                    PressingShortcut = PressingShortcut with { Modifiers = _pressedKeyModifiers };
                    PressingShortcutChanged?.Invoke(this, PressingShortcut);
                    break;
                }
                case false:
                {
                    _pressedKeyModifiers &= ~keyModifiers;
                    if (_pressedKeyModifiers == KeyModifiers.None)
                    {
                        if (PressingShortcut.Modifiers != KeyModifiers.None && PressingShortcut.Key == Key.None)
                        {
                            PressingShortcut = default; // modifiers only shortcut, reset it
                        }

                        if (PressingShortcut.Modifiers == KeyModifiers.None)
                        {
                            PressingShortcut = default; // no modifiers, reset it
                        }

                        // system key is all released, capture is done
                        PressingShortcutChanged?.Invoke(this, PressingShortcut);
                        ShortcutFinished?.Invoke(this, PressingShortcut);
                    }
                    break;
                }
            }

            blockNext = true;
        }

        public void Dispose()
        {
            if (IsDisposed) return;
            IsDisposed = true;

            _hookSubscription.Dispose();
        }
    }
}
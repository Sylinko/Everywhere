using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Interop;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Windows <see cref="IOverlayDismissWatcher"/> implementation over <see cref="LowLevelHook"/>.
/// </summary>
/// <remarks>
/// <para>
/// The mouse and keyboard hooks are installed while at least one watch exists and removed when the last
/// one is disposed, so no hook exists while no overlay is visible. A watch that merely moves calls
/// <see cref="IOverlayDismissWatch.Update"/> and keeps the hooks in place: reinstalling them per move
/// would recreate a dedicated hook thread each time and add latency to all input.
/// </para>
/// <para>
/// Hook callbacks arrive on the hook thread while <see cref="Watch"/>, <see cref="IOverlayDismissWatch.Update"/>
/// and disposal are called from the UI thread, hence the lock around <see cref="_registrations"/>. Nothing on
/// the hook path may perform I/O — including logging — because Windows drops hooks that exceed
/// <c>LowLevelHooksTimeout</c>, and a blocking hook stalls the entire system input queue.
/// </para>
/// </remarks>
public sealed class OverlayDismissWatcher : IOverlayDismissWatcher
{
    public bool IsSupported => true;

    /// <summary>Set by Windows on mouse hook events synthesised through <c>SendInput</c>.</summary>
    private const uint LLMHF_INJECTED = 0x01;

    private readonly Lock _syncLock = new();
    private readonly List<Registration> _registrations = [];

    private IDisposable? _mouseHook;
    private IDisposable? _keyboardHook;

    public IOverlayDismissWatch Watch(PixelRect bounds, Action onDismiss)
    {
        var registration = new Registration(this, bounds, onDismiss);

        using var _ = _syncLock.EnterScope();

        _registrations.Add(registration);
        if (_registrations.Count == 1)
        {
            _mouseHook = LowLevelHook.CreateMouseHook(OnMouseInput);
            _keyboardHook = LowLevelHook.CreateKeyboardHook(OnKeyboardInput);
        }

        return registration;
    }

    private void Remove(Registration registration)
    {
        using var _ = _syncLock.EnterScope();

        if (!_registrations.Remove(registration) || _registrations.Count > 0) return;

        // Last watch is gone: tear the hooks down so we stop observing global input entirely.
        _mouseHook?.Dispose();
        _mouseHook = null;
        _keyboardHook?.Dispose();
        _keyboardHook = null;
    }

    /// <remarks>
    /// Runs on the hook thread. Never sets <paramref name="blockNext"/>: a click that dismisses an
    /// overlay must still reach whatever is underneath it.
    /// </remarks>
    private void OnMouseInput(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        if ((hookStruct.flags & LLMHF_INJECTED) != 0) return;

        switch (msg)
        {
            case WINDOW_MESSAGE.WM_LBUTTONDOWN:
            case WINDOW_MESSAGE.WM_RBUTTONDOWN:
            case WINDOW_MESSAGE.WM_MBUTTONDOWN:
            case WINDOW_MESSAGE.WM_XBUTTONDOWN:
            {
                // A press inside the overlay is the user clicking one of its own buttons.
                var point = new PixelPoint(hookStruct.pt.X, hookStruct.pt.Y);
                DismissWhere(r => !r.Bounds.Contains(point));
                break;
            }

            case WINDOW_MESSAGE.WM_MOUSEWHEEL:
            case WINDOW_MESSAGE.WM_MOUSEHWHEEL:
            {
                // Once content scrolls, the anchor the overlay was positioned against is stale.
                DismissWhere(_ => true);
                break;
            }
        }
    }

    /// <remarks>Runs on the hook thread.</remarks>
    private void OnKeyboardInput(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        // Ignore synthesised keys. The Windows text selection detector falls back to sending Ctrl+C to
        // read a selection it could not obtain through UI Automation; treating that as user input would
        // dismiss the overlay in the middle of the very capture that produced it.
        if ((hookStruct.flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0) return;

        if (msg is not (WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN)) return;
        if (IsSelectionModifier((VIRTUAL_KEY)hookStruct.vkCode)) return;

        DismissWhere(_ => true);
    }

    /// <summary>
    /// Keys that extend or act on an existing selection, and so must not dismiss the overlay.
    /// Without this, Shift+arrow to extend a selection would dismiss mid-gesture.
    /// </summary>
    private static bool IsSelectionModifier(VIRTUAL_KEY key) => key is
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT or
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL or
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU or
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN;

    private void DismissWhere(Func<Registration, bool> predicate)
    {
        var matching = Array.Empty<Registration>();
        using (_syncLock.EnterScope())
        {
            if (_registrations.Count == 0) return;

            matching = [.._registrations.Where(predicate)];
        }

        foreach (var registration in matching)
        {
            registration.Dismiss();
        }
    }

    private sealed class Registration(OverlayDismissWatcher owner, PixelRect bounds, Action onDismiss) : IOverlayDismissWatch
    {
        /// <summary>Read on the hook thread, written on the UI thread; see the type-level remarks.</summary>
        public PixelRect Bounds => _bounds;

        private PixelRect _bounds = bounds;
        private bool _completed;
        private bool _disposed;

        public void Update(PixelRect bounds)
        {
            using var _ = owner._syncLock.EnterScope();

            if (_disposed) return;

            _bounds = bounds;

            // Re-arm: the overlay moved to a new anchor, so a previous dismissal no longer applies.
            _completed = false;
        }

        /// <summary>
        /// Invokes the dismissal callback at most once per arming, and never after disposal.
        /// </summary>
        /// <remarks>
        /// The callback runs while the owner's lock is held. Deciding under the lock and invoking outside
        /// it would reintroduce the race this guards against: disposal could land in between, and the
        /// callback would fire for a watch the caller has already abandoned — which, because the caller
        /// hides whatever toolbar is current rather than one identified by this watch, could hide a newer
        /// toolbar. The callback is contractually cheap and non-blocking, so holding the lock is safe.
        /// </remarks>
        public void Dismiss()
        {
            using var _ = owner._syncLock.EnterScope();

            if (_completed || _disposed) return;

            _completed = true;
            onDismiss();
        }

        public void Dispose()
        {
            using (owner._syncLock.EnterScope())
            {
                _disposed = true;
            }

            owner.Remove(this);
        }
    }
}

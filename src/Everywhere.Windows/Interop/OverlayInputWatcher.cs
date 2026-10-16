using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Observes global pointer and dismissal input through low-level Windows hooks.
/// </summary>
/// <remarks>
/// Hooks exist only while an overlay is visible. Callbacks run on dedicated hook threads and must
/// remain cheap and non-blocking: Windows can remove a hook that stalls the system input queue.
/// Updating overlay bounds keeps the existing hooks in place rather than recreating their threads.
/// </remarks>
public sealed class OverlayInputWatcher : OverlayInputWatcherBase
{
    public override bool IsSupported => true;

    /// <summary>Set by Windows on mouse hook events synthesized through SendInput.</summary>
    private const uint InjectedMouseFlag = 0x01;

    private IDisposable? _mouseHook;
    private IDisposable? _keyboardHook;

    /// <inheritdoc />
    protected override void StartWatching()
    {
        _mouseHook = LowLevelHook.CreateMouseHook(OnMouseInput);
        _keyboardHook = LowLevelHook.CreateKeyboardHook(OnKeyboardInput);
        if (PInvoke.GetCursorPos(out var point)) UpdatePointerPosition(new PixelPoint(point.X, point.Y));
    }

    /// <inheritdoc />
    protected override void StopWatching()
    {
        DisposeHelper.DisposeToDefault(ref _mouseHook);
        DisposeHelper.DisposeToDefault(ref _keyboardHook);
    }

    /// <remarks>
    /// Never suppress input. A click dismissing an overlay must still reach the underlying window.
    /// </remarks>
    private void OnMouseInput(WINDOW_MESSAGE msg, ref MSLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        var point = new PixelPoint(hookStruct.pt.X, hookStruct.pt.Y);
        UpdatePointerPosition(point);
        if ((hookStruct.flags & InjectedMouseFlag) != 0) return;

        switch (msg)
        {
            case WINDOW_MESSAGE.WM_LBUTTONDOWN:
            case WINDOW_MESSAGE.WM_RBUTTONDOWN:
            case WINDOW_MESSAGE.WM_MBUTTONDOWN:
            case WINDOW_MESSAGE.WM_XBUTTONDOWN:
                HandlePointerPressed(point);
                break;

            case WINDOW_MESSAGE.WM_MOUSEWHEEL:
            case WINDOW_MESSAGE.WM_MOUSEHWHEEL:
                HandlePointerScrolled(point);
                break;
        }
    }

    private void OnKeyboardInput(WINDOW_MESSAGE msg, ref KBDLLHOOKSTRUCT hookStruct, ref bool blockNext)
    {
        // Copy fallback synthesizes Ctrl+C during selection capture. It must not dismiss its own toolbar.
        if ((hookStruct.flags & KBDLLHOOKSTRUCT_FLAGS.LLKHF_INJECTED) != 0) return;
        if (msg is not (WINDOW_MESSAGE.WM_KEYDOWN or WINDOW_MESSAGE.WM_SYSKEYDOWN)) return;

        var key = (VIRTUAL_KEY)hookStruct.vkCode;
        if (IsSelectionModifier(key)) return;

        var isMenuNavigation = key is VIRTUAL_KEY.VK_UP or VIRTUAL_KEY.VK_DOWN or VIRTUAL_KEY.VK_LEFT or VIRTUAL_KEY.VK_RIGHT or
            VIRTUAL_KEY.VK_HOME or VIRTUAL_KEY.VK_END or VIRTUAL_KEY.VK_PRIOR or VIRTUAL_KEY.VK_NEXT or
            VIRTUAL_KEY.VK_RETURN or VIRTUAL_KEY.VK_SPACE or VIRTUAL_KEY.VK_ESCAPE or VIRTUAL_KEY.VK_TAB;
        HandleKeyPressed(isMenuNavigation);
    }

    /// <summary>
    /// Modifiers may extend or act on the selection and must not dismiss the overlay mid-gesture.
    /// </summary>
    private static bool IsSelectionModifier(VIRTUAL_KEY key) => key is
        VIRTUAL_KEY.VK_SHIFT or VIRTUAL_KEY.VK_LSHIFT or VIRTUAL_KEY.VK_RSHIFT or
        VIRTUAL_KEY.VK_CONTROL or VIRTUAL_KEY.VK_LCONTROL or VIRTUAL_KEY.VK_RCONTROL or
        VIRTUAL_KEY.VK_MENU or VIRTUAL_KEY.VK_LMENU or VIRTUAL_KEY.VK_RMENU or
        VIRTUAL_KEY.VK_LWIN or VIRTUAL_KEY.VK_RWIN;
}
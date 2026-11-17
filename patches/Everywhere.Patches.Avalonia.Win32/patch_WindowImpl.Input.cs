using Avalonia.Input;
using Avalonia.Input.TextInput;
using Everywhere.Patches.Contracts.Interop;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Everywhere.Patches.Avalonia.Win32;

internal partial class patch_WindowImpl : IWindowInputFeature
{
    private const GCS ResultCompositionFlags =
        GCS.GCS_RESULTSTR |
        GCS.GCS_RESULTREADSTR |
        GCS.GCS_RESULTCLAUSE |
        GCS.GCS_RESULTREADCLAUSE;

    // A zero-initialized field preserves the original policy without modifying native constructors.
    public bool CanTakeFocusOnClick
    {
        get => !_everywherePreventMouseFocus;
        set => _everywherePreventMouseFocus = !value;
    }

    public ITextInputMethodImpl? TextInputMethod { get; set; }

    public IInputRoot? InputRoot => _owner;

    private bool _everywherePreventMouseFocus;

    protected virtual bool ShouldTakeFocusOnClick => !_everywherePreventMouseFocus;

    private extern object? orig_TryGetFeature(Type featureType);

    public object? TryGetFeature(Type featureType)
    {
        if (featureType == typeof(ITextInputMethodImpl) && TextInputMethod is { } method)
        {
            return method;
        }

        return orig_TryGetFeature(featureType);
    }

    private extern IntPtr orig_AppWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    protected virtual IntPtr AppWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if ((WindowsMessage)msg != WindowsMessage.WM_IME_COMPOSITION)
        {
            return orig_AppWndProc(hWnd, msg, wParam, lParam);
        }

        // When IMM32 composition commits on WM_KEYUP (e.g. tapping Shift to toggle English mode
        // in Chinese IMEs), TranslateMessage dispatches WM_IME_COMPOSITION synchronously before
        // DispatchMessage delivers WM_KEYUP(VK_PROCESSKEY), which clears _ignoreWmChar before
        // any posted WM_CHAR messages are dequeued. Strip consumed GCS_RESULT* flags so
        // DefWindowProc never synthesizes duplicate WM_IME_CHAR / WM_CHAR messages, while still
        // forwarding any remaining preedit flags (such as GCS_COMPSTR) to the default IME window.
        var timestamp = unchecked((uint)GetMessageTime());
        if (!patch_Imm32InputMethod.Current.TryHandleComposition(wParam, lParam, timestamp))
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        var remainingLParam = lParam & ~(nint)(int)ResultCompositionFlags;
        if (remainingLParam == 0 && lParam != 0)
        {
            return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, remainingLParam);
    }
}

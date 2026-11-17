using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.Win32;
using Avalonia.Win32.Input;
using MonoMod;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Everywhere.Patches.Avalonia.Win32;

/// <summary>
/// Patches Avalonia's IMM32 input method so consumed composition results are reported
/// back to <see cref="patch_WindowImpl"/> instead of relying on <c>_ignoreWmChar</c>
/// to block duplicate <c>WM_CHAR</c> messages synthesized by <c>DefWindowProc</c>.
/// </summary>
[MonoModPatch("Avalonia.Win32.Input.Imm32InputMethod")]
internal class patch_Imm32InputMethod
{
    [MonoModIgnore]
    public static extern patch_Imm32InputMethod Current { get; }

    [MonoModIgnore]
    public extern IntPtr Hwnd { get; }

    [MonoModIgnore]
    public extern TextInputMethodClient? Client { get; }

    [MonoModIgnore]
    public extern bool IsActive { get; }

    [MonoModIgnore]
    public extern bool IsComposing { get; set; }

    [MonoModIgnore]
    public extern string? Composition { get; internal set; }

    [MonoModIgnore]
    private WindowImpl? _parent;

    [MonoModIgnore]
    private bool _ignoreComposition;

    [MonoModIgnore]
    private int? _compositionCursorPosition;

    [MonoModReplace]
    public string? GetCompositionString(GCS flag)
    {
        if (!IsComposing && flag != GCS.GCS_RESULTSTR)
        {
            return null;
        }

        var himc = ImmGetContext(Hwnd);
        if (himc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ImmGetCompositionString(himc, flag);
        }
        finally
        {
            ImmReleaseContext(Hwnd, himc);
        }
    }

    [MonoModReplace]
    public void HandleComposition(IntPtr wParam, IntPtr lParam, uint timestamp) =>
        TryHandleComposition(wParam, lParam, timestamp);

    /// <summary>
    /// Processes <c>WM_IME_COMPOSITION</c> and returns <see langword="true"/> when the
    /// composition result in <paramref name="lParam"/> has been consumed or intentionally
    /// discarded and must not be forwarded to <c>DefWindowProc</c>.
    /// </summary>
    internal bool TryHandleComposition(IntPtr wParam, IntPtr lParam, uint timestamp)
    {
        var flags = (GCS)unchecked((int)lParam.ToInt64());
        if (_ignoreComposition)
        {
            // Reset() forces CPS_COMPLETE to clear active IME state and sets _ignoreComposition
            // (along with _ignoreWmChar) so the resulting commit is discarded. Suppressing
            // GCS_RESULT* before DefWindowProc prevents fallback WM_CHAR generation, so clear
            // _ignoreWmChar as well to avoid swallowing unrelated subsequent character input.
            _ignoreComposition = false;
            _parent?._ignoreWmChar = false;
            return (flags & GCS.GCS_RESULTSTR) != 0;
        }

        if (flags == 0)
        {
            CompositionChanged(string.Empty, null);
        }

        var hasResultChanged = (flags & GCS.GCS_RESULTSTR) != 0;
        var hasConsumedResult = false;
        if (hasResultChanged)
        {
            var resultString = GetCompositionString(GCS.GCS_RESULTSTR);

            Composition = null;
            _compositionCursorPosition = null;

            if (IsActive && Client is { } client)
            {
                client.SetPreeditText(null, null);
            }

            if (_parent is { Input: { } input } parent && !string.IsNullOrEmpty(resultString))
            {
                input(new RawTextInputEventArgs(WindowsKeyboardDevice.Instance, timestamp, parent.Owner, resultString));
                hasConsumedResult = true;
            }
        }

        var hasCompositionChanged = (flags & GCS.GCS_COMPSTR) != 0;
        var hasCursorPositionChanged = (flags & GCS.GCS_CURSORPOS) != 0;
        if (!hasCompositionChanged && (!hasCursorPositionChanged || hasResultChanged))
        {
            return hasConsumedResult;
        }

        var compositionString = hasCompositionChanged ? GetCompositionString(GCS.GCS_COMPSTR) : Composition;
        var cursorPosition = hasCursorPositionChanged ? GetCompositionCursorPosition() : _compositionCursorPosition;
        CompositionChanged(compositionString, cursorPosition);
        return hasConsumedResult;
    }

    [MonoModIgnore]
    public extern void CompositionChanged(string? composition, int? cursorPosition);

    [MonoModIgnore]
    private extern int? GetCompositionCursorPosition();
}

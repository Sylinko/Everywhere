using MonoMod;

// Avalonia's Win32 interop container is internal. These declarations provide the
// compile-time shapes consumed by patch_WindowImpl and patch_Imm32InputMethod; MonoMod
// relinks them to the matching members in Avalonia.Win32 when weaving the assembly.
namespace Avalonia.Win32.Interop;

[MonoModIgnore]
internal static class UnmanagedMethods
{
    [MonoModIgnore]
    public enum WindowStyles : uint;

    [MonoModIgnore]
    public enum WindowsMessage : uint
    {
        WM_IME_COMPOSITION = 0x010F,
    }

    [Flags]
    [MonoModIgnore]
    public enum GCS : uint
    {
        GCS_COMPSTR = 0x0008,
        GCS_CURSORPOS = 0x0080,
        GCS_RESULTREADSTR = 0x0200,
        GCS_RESULTREADCLAUSE = 0x0400,
        GCS_RESULTSTR = 0x0800,
        GCS_RESULTCLAUSE = 0x1000,
    }

    [MonoModIgnore]
    public struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [MonoModIgnore]
    public static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [MonoModIgnore]
    public static extern int GetMessageTime();

    [MonoModIgnore]
    public static extern IntPtr ImmGetContext(IntPtr hWnd);

    [MonoModIgnore]
    public static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [MonoModIgnore]
    public static extern string? ImmGetCompositionString(IntPtr hIMC, GCS dwIndex);
}

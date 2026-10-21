using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace Everywhere.Windows.Interop;

internal static class TextInputNative
{
    public static void MakeNonActivating(nint hwnd)
    {
        SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x08000080);
    }

    // Both IME windows and interactive Avalonia popups must belong to this capture's UI thread.
    // Owned popup chains cover assistant submenus without consulting Avalonia on the hook thread.
    public static bool IsInputPopup(PixelPoint point, uint inputThread, nint targetHandle)
    {
        var hwnd = WindowFromPoint(new NativePoint { X = point.X, Y = point.Y });
        hwnd = GetAncestor(hwnd, 2);
        if (hwnd == 0 || GetWindowThreadProcessId(hwnd, out _) != inputThread)
        {
            return false;
        }

        // Use the actual native pointer target and its ownership chain, not rectangles for passive
        // preedit/tooltips. Another application window on the same thread is not an input popup.
        if (targetHandle != 0 && hwnd != targetHandle && GetAncestor(hwnd, 3) == targetHandle)
        {
            return true;
        }

        var name = new StringBuilder(256);
        GetClassNameW(hwnd, name, name.Capacity);
        return name.ToString().Contains("IME", StringComparison.OrdinalIgnoreCase);
    }

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern nint GetFocus();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(nint hwnd, StringBuilder name, int capacity);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}

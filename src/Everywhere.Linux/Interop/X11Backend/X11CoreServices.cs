using System.Runtime.InteropServices;
using Avalonia.Input;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Provides core X11 services: Property reading, Coordinate translation, Atom resolution, Key conversion, etc.
/// </summary>
public sealed class X11CoreServices(X11Context context)
{
    private readonly IntPtr _display = context.Display;

    public Atom GetAtom(string name, bool onlyIfExists = false) => context.GetAtom(name, onlyIfExists);

    public KeyCode XKeycode(Key key)
    {
        var ks = Xlib.XStringToKeysym(key.ToString());
        KeyCode keycode = 0;
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        if (keycode != 0) return keycode;

        ks = Xlib.XStringToKeysym(key.ToString().ToUpperInvariant());
        if (ks != 0) keycode = Xlib.XKeysymToKeycode(_display, ks);
        return keycode;
    }

    public Key KeycodeToAvaloniaKey(KeyCode keycode)
    {
        try
        {
            var ks = Xlib.XKeycodeToKeysym(_display, keycode, 0);
            var name = KeySymToString(ks);
            if (!string.IsNullOrEmpty(name) && name.Length == 1 && char.IsLetter(name[0]))
                return Enum.Parse<Key>(name.ToUpperInvariant());
            return Key.None;
        }
        catch { return Key.None; }
    }

    private static string KeySymToString(KeySym ks)
    {
        try
        {
            var p = X11Native.XKeysymToString(ks);
            return p == IntPtr.Zero ? string.Empty : Marshal.PtrToStringAnsi(p) ?? string.Empty;
        }
        catch { return string.Empty; }
    }

    public void GetProperty(X11Window window, string propertyName, long length, Atom reqType, Action<Atom, int, ulong, ulong, IntPtr> callback)
    {
        var atom = GetAtom(propertyName, true);
        if (atom == Atom.None) return;

        X11Native.XGetWindowProperty(
            _display,
            window,
            atom,
            0,
            length,
            0,
            reqType,
            out var actualType,
            out var actualFormat,
            out var nItems,
            out var bytesAfter,
            out var prop);

        try
        {
            callback(actualType, actualFormat, nItems, bytesAfter, prop);
        }
        finally
        {
            Xlib.XFree(prop);
        }
    }

    public bool TranslateCoordinates(X11Window src, X11Window dst, int srcX, int srcY, out int dstX, out int dstY)
    {
        var result = X11Native.XTranslateCoordinates(_display, src, dst, srcX, srcY, out dstX, out dstY, out _);
        return result != 0;
    }
}
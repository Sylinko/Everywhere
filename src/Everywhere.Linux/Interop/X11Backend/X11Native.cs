using System.Runtime.InteropServices;
using X11;
using X11Window = X11.Window;

namespace Everywhere.Linux.Interop.X11Backend;

/// <summary>
/// Native P/Invoke definitions for X11 and related extensions.
/// </summary>
public static partial class X11Native
{
    public const string LibX11 = "libX11.so.6";
    public const string LibXFixes = "libXfixes.so.3";
    public const X11Window ScanSkipWindow = (X11Window)ulong.MaxValue;
    public const uint CurrentTime = 0;

    public enum ShapeKind
    {
        Bounding = 0,
        Clip = 1,
        Input = 2
    }

    [LibraryImport(LibX11)]
    internal static partial void XConvertSelection(
        IntPtr display,
        Atom selection,
        Atom target,
        Atom property,
        X11Window requestor,
        uint time);

    [LibraryImport(LibX11)]
    internal static partial IntPtr XGetWMHints(IntPtr display, X11Window window);

    [LibraryImport(LibX11)]
    internal static partial void XSetWMHints(IntPtr display, X11Window window, ref XWMHints hints);

    [LibraryImport(LibX11)]
    internal static partial int XScreenCount(IntPtr display);

    [LibraryImport(LibX11)]
    internal static partial int XDisplayWidth(IntPtr display, int screenNumber);

    [LibraryImport(LibX11)]
    internal static partial int XDisplayHeight(IntPtr display, int screenNumber);

    [LibraryImport(LibX11)]
    internal static partial IntPtr XKeysymToString(KeySym keySym);

    [LibraryImport(LibX11)]
    internal static partial void XQueryKeymap(IntPtr display, [Out] byte[] keymap);

    [LibraryImport(LibX11)]
    internal static partial int XGrabKeyboard(
        IntPtr display,
        X11Window grabWindow,
        int ownerEvents,
        GrabMode pointerMode,
        GrabMode keyboardMode,
        uint time);

    [LibraryImport(LibX11)]
    internal static partial int XUngrabKeyboard(IntPtr display, X11Window grabWindow);

    [LibraryImport(LibX11)]
    internal static partial int XTranslateCoordinates(
        IntPtr display,
        X11Window srcWindow,
        X11Window destWindow,
        int srcX,
        int srcY,
        out int destXReturn,
        out int destYReturn,
        out IntPtr childReturn);

    [LibraryImport(LibX11)]
    internal static partial int XGetWindowProperty(
        IntPtr display,
        X11Window window,
        Atom property,
        long offset,
        long length,
        int delete,
        Atom reqType,
        out Atom actualTypeReturn,
        out int actualFormatReturn,
        out ulong nItemsReturn,
        out ulong bytesAfterReturn,
        out IntPtr propReturn);

    [LibraryImport(LibX11)]
    internal static partial int XChangeWindowAttributes(
        IntPtr display,
        X11Window window,
        ulong valueMask,
        ref XSetWindowAttributes attributes);

    [LibraryImport(LibX11)]
    internal static unsafe partial int XSendEvent(
        IntPtr display,
        X11Window window,
        int propagate,
        ulong eventMask,
        XClientMessageEvent* eventSend);

    [LibraryImport(LibXFixes)]
    internal static partial IntPtr XFixesCreateRegion(IntPtr display, [In] XRectangle[] rectangles, int nRectangles);

    [LibraryImport(LibXFixes)]
    internal static partial void XFixesDestroyRegion(IntPtr display, IntPtr region);

    [LibraryImport(LibXFixes)]
    internal static partial void XFixesSetWindowShapeRegion(
        IntPtr display,
        X11Window window,
        int shapeKind,
        int xOffset,
        int yOffset,
        IntPtr region);

    [LibraryImport(LibXFixes)]
    internal static partial int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);

    [LibraryImport(LibXFixes)]
    internal static partial void XFixesSelectSelectionInput(
        IntPtr display,
        X11Window window,
        Atom selection,
        uint eventMask);

    public const string LibXi = "libXi.so.6";

    [LibraryImport(LibXi)]
    internal static partial int XIQueryVersion(IntPtr display, ref int major, ref int minor);

    [LibraryImport(LibXi)]
    internal static partial int XISelectEvents(IntPtr display, X11Window window, [In] XIEventMask[] masks, int numMasks);

    [LibraryImport(LibX11)]
    internal static partial int XGetEventData(IntPtr display, IntPtr cookie);

    [LibraryImport(LibX11)]
    internal static partial void XFreeEventData(IntPtr display, IntPtr cookie);

    public const int XI_AllDevices = 0;
    public const int XI_AllMasterDevices = 1;

    public const int XI_ButtonPress = 4;
    public const int XI_ButtonRelease = 5;

    public const int XFixesSelectionNotify = 0;
    public const uint XFixesSetSelectionOwnerNotifyMask = 1 << 0;

    public enum XFixesSelectionEventMask : ulong
    {
        SetSelectionOwnerMask = 1 << 0,
        SelectionWindowDestroyMask = 1 << 1,
        SelectionClientCloseMask = 1 << 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIEventMask
    {
        public int deviceid;
        public int mask_len;
        public IntPtr mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIButtonState
    {
        public int mask_len;
        public IntPtr mask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIValuatorState
    {
        public int mask_len;
        public IntPtr mask;
        public IntPtr values;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIModifierState
    {
        public int @base;
        public int latched;
        public int locked;
        public int effective;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIGroupState
    {
        public int @base;
        public int latched;
        public int locked;
        public int effective;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIDeviceEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public int extension;
        public int evtype;
        public uint time;
        public int deviceid;
        public int sourceid;
        public int detail;
        public X11Window root;
        public X11Window event_window;
        public X11Window child;
        public double root_x;
        public double root_y;
        public double event_x;
        public double event_y;
        public int flags;
        public XIButtonState buttons;
        public XIValuatorState valuators;
        public XIModifierState mods;
        public XIGroupState group;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XGenericEventCookie
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public int extension;
        public int evtype;
        public uint cookie;
        public IntPtr data;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XFixesSelectionNotifyEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public X11Window window;
        public Atom subtype;
        public X11Window owner;
        public Atom selection;
        public uint time;
        public uint selection_time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSelectionEvent
    {
        public int type;
        public ulong serial;
        public int send_event;
        public IntPtr display;
        public X11Window requestor;
        public Atom selection;
        public Atom target;
        public Atom property;
        public uint time;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XSetWindowAttributes
    {
        public IntPtr background_pixmap;
        public ulong background_pixel;
        public IntPtr border_pixmap;
        public ulong border_pixel;
        public int bit_gravity;
        public int win_gravity;
        public int backing_store;
        public ulong backing_planes;
        public ulong backing_pixel;
        public int save_under;
        public ulong event_mask;
        public ulong do_not_propagate_mask;
        public int override_redirect;
        public IntPtr colormap;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short x;
        public short y;
        public ushort width;
        public ushort height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XWMHints
    {
        public IntPtr flags;
        public int input;
        public int initial_state;
        public IntPtr icon_pixmap;
        public IntPtr icon_window;
        public int icon_x;
        public int icon_y;
        public IntPtr icon_mask;
        public IntPtr window_group;
    }

    public enum MapState
    {
        IsUnmapped = 0,
        IsUnviewable = 1,
        IsViewable = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XClientMessageEvent
    {
        public int type;
        public UIntPtr serial;
        public int send_event;
        public IntPtr display;
        public IntPtr window;
        public IntPtr message_type;
        public int format;
        public IntPtr data_l0;
        public IntPtr data_l1;
        public IntPtr data_l2;
        public IntPtr data_l3;
        public IntPtr data_l4;
    }

    public static string GetErrorCodeName(int code)
    {
        return code switch
        {
            1 => "BadRequest",
            2 => "BadValue",
            3 => "BadWindow",
            4 => "BadPixmap",
            5 => "BadAtom",
            6 => "BadCursor",
            7 => "BadFont",
            8 => "BadMatch",
            9 => "BadDrawable",
            10 => "BadAccess",
            11 => "BadAlloc",
            12 => "BadColor",
            13 => "BadGC",
            14 => "BadIDChoice",
            15 => "BadName",
            16 => "BadLength",
            17 => "BadImplementation",
            _ => $"Unknown({code})"
        };
    }

    public static string GetRequestCodeName(int requestCode)
    {
        return requestCode switch
        {
            // Core protocol requests
            1 => "CreateWindow",
            2 => "ChangeWindowAttributes",
            3 => "GetWindowAttributes",
            4 => "DestroyWindow",
            5 => "DestroySubwindows",
            6 => "ChangeSaveSet",
            7 => "ReparentWindow",
            8 => "MapWindow",
            9 => "MapSubwindows",
            10 => "UnmapWindow",
            11 => "UnmapSubwindows",
            12 => "ConfigureWindow",
            13 => "CirculateWindow",
            14 => "GetGeometry",
            15 => "QueryTree",
            16 => "InternAtom",
            17 => "GetAtomName",
            18 => "ChangeProperty",
            19 => "DeleteProperty",
            20 => "GetProperty",
            21 => "ListProperties",
            22 => "SetSelectionOwner",
            23 => "GetSelectionOwner",
            24 => "ConvertSelection",
            25 => "SendEvent",
            26 => "GrabPointer",
            27 => "UngrabPointer",
            28 => "GrabButton",
            29 => "UngrabButton",
            30 => "ChangeActivePointerGrab",
            31 => "GrabKeyboard",
            32 => "UngrabKeyboard",
            33 => "GrabKey",
            34 => "Ungrab",
            35 => "AllowEvents",
            36 => "GrabServer",
            37 => "UngrabServer",
            38 => "QueryPointer",
            39 => "GetMotionEvents",
            40 => "TranslateCoords",
            41 => "WarpPointer",
            42 => "SetInputFocus",
            43 => "GetInputFocus",
            44 => "QueryKeymap",
            45 => "OpenFont",
            46 => "CloseFont",
            47 => "QueryFont",
            48 => "QueryTextExtents",
            49 => "ListFonts",
            50 => "ListFontsWithInfo",
            51 => "SetFontPath",
            52 => "GetFontPath",
            53 => "CreatePixmap",
            54 => "FreePixmap",
            55 => "CreateGC",
            56 => "ChangeGC",
            57 => "CopyGC",
            58 => "SetDashes",
            59 => "SetClipRectangles",
            60 => "FreeGC",
            61 => "ClearArea",
            62 => "CopyArea",
            63 => "CopyPlane",
            64 => "PolyPoint",
            65 => "PolyLine",
            66 => "PolySegment",
            67 => "PolyRectangle",
            68 => "PolyArc",
            69 => "FillPoly",
            70 => "PolyFillRectangle",
            71 => "PolyFillArc",
            72 => "PutImage",
            73 => "GetImage",
            74 => "PolyText8",
            75 => "PolyText16",
            76 => "ImageText8",
            77 => "ImageText16",
            78 => "CreateColormap",
            79 => "FreeColormap",
            80 => "CopyColormapAndFree",
            81 => "InstallColormap",
            82 => "UninstallColormap",
            83 => "ListInstalledColormaps",
            84 => "AllocColor",
            85 => "AllocNamedColor",
            86 => "AllocColorCells",
            87 => "AllocColorPlanes",
            88 => "FreeColors",
            89 => "StoreColors",
            90 => "StoreNamedColor",
            91 => "QueryColors",
            92 => "LookupColor",
            93 => "CreateCursor",
            94 => "CreateGlyphCursor",
            95 => "FreeCursor",
            96 => "RecolorCursor",
            97 => "QueryBestSize",
            98 => "QueryExtension",
            99 => "ListExtensions",
            100 => "ChangeKeyboardMapping",
            101 => "GetKeyboardMapping",
            102 => "ChangeKeyboardControl",
            103 => "GetKeyboardControl",
            104 => "Bell",
            105 => "ChangePointerControl",
            106 => "GetPointerControl",
            107 => "SetScreenSaver",
            108 => "GetScreenSaver",
            109 => "ChangeHosts",
            110 => "ListHosts",
            111 => "SetAccessControl",
            112 => "SetCloseDownMode",
            113 => "KillClient",
            114 => "RotateProperties",
            115 => "ForceScreenSaver",
            116 => "SetPointerMapping",
            117 => "GetPointerMapping",
            118 => "SetModifierMapping",
            119 => "GetModifierMapping",
            120 => "NoOperation",

            // Common extensions
            128 => "X_QueryExtension", // Usually for extensions
            129 => "X_ListExtensions",

            // XFixes extension (major codes vary, but common ones)
            140 => "XFixes_QueryVersion",
            141 => "XFixes_SetWindowShapeRegion",

            // XShape extension
            142 => "XShape_QueryVersion",
            143 => "XShape_Rectangles",

            _ => $"UnknownRequest{requestCode}"
        };
    }
}
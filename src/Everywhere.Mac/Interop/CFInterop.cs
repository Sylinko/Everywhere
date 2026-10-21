using System.Runtime.InteropServices;

namespace Everywhere.Mac.Interop;

internal static partial class CFInterop
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFHash(nint cf);

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFGetTypeID(nint cf);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CFEqual(nint first, nint second);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRetain(nint cf);

    /// <summary>
    /// Creates a version-zero run-loop source with a perform callback. The caller
    /// owns the returned handle and the lifetime of the callback's context.
    /// </summary>
    public static unsafe nint CreateRunLoopSource(nint info, delegate* unmanaged<nint, void> perform)
    {
        var context = new CFRunLoopSourceContext(info, perform);
        return CFRunLoopSourceCreate(IntPtr.Zero, 0, ref context);
    }

    [LibraryImport(CoreFoundation)]
    private static partial nint CFRunLoopSourceCreate(nint allocator, nint order, ref CFRunLoopSourceContext context);

    // All ten pointer-sized slots are required by CoreFoundation's version-zero
    // ABI. Schedule/cancel are optional; ShutdownSource's implementations are empty.
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CFRunLoopSourceContext
    {
        private nint _version;
        private nint _info;
        private nint _retain;
        private nint _release;
        private nint _copyDescription;
        private nint _equal;
        private nint _hash;
        private nint _schedule;
        private nint _cancel;
        private delegate* unmanaged<nint, void> _perform;

        public CFRunLoopSourceContext(nint info, delegate* unmanaged<nint, void> perform)
        {
            _info = info;
            _perform = perform;
        }
    }
}
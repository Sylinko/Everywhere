using System.Runtime.InteropServices;

namespace Everywhere.Mac.Interop;

internal static partial class CFInterop
{
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    public static partial nuint CFHash(nint cf);

    [LibraryImport(CoreFoundation)]
    public static partial void CFRelease(IntPtr cf);

    [LibraryImport(CoreFoundation)]
    public static partial nint CFRetain(nint cf);
}
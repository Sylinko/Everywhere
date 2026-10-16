using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Everywhere.Extensions;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

internal static partial class CGInterop
{
    /// <summary>
    /// Marks selection copy fallback input across Main and Automation Host without relying on process identity.
    /// </summary>
    public const long SelectionCopyEventTag = 0x455753454C434F50;

    // ReSharper disable once InconsistentNaming
    [field: AllowNull, MaybeNull]
    private static ConstructorInfo CGEventConstructorInfo =>
        field ??= typeof(CGEvent).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, [typeof(NativeHandle), typeof(bool)]).NotNull();

    /// <summary>
    /// `CGEvent(NativeHandle)` is mistakenly not compiled with `!NET` directive, making it inaccessible in .NET 5+ builds.
    /// We use reflection to access the other internal constructor `CGEvent(NativeHandle, bool)` instead.
    /// </summary>
    /// <param name="cgEventRef"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public static CGEvent CGEventFromHandle(nint cgEventRef)
    {
        return CGEventConstructorInfo.Invoke([new NativeHandle(cgEventRef), false]) as CGEvent
            ?? throw new InvalidOperationException("Failed to create CGEvent from handle.");
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(CoreGraphics)]
    public static partial nint CGWindowListCopyWindowInfo(CGWindowListOption option, uint relativeToWindow);
}
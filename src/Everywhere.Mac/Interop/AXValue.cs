using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CoreFoundation;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Represents the type of an accessibility value object in macOS.
/// </summary>
public enum AXValueType
{
    ValueIllegal = 0,
    CGPoint = 1,
    CGSize = 2,
    CGRect = 3,
    CFRange = 4,
    AXError = 5,
}

/// <summary>
/// Represents an accessibility value object in macOS.
/// </summary>
public partial class AXValue : NSObject
{
    internal static nuint TypeId => AXValueGetTypeID();

    public AXValueType Type { get; }

    public CGPoint Point { get; }

    public CGSize Size { get; }

    public CGRect Rect { get; }

    public CFRange Range { get; }

    public AXError Error { get; }

    public AXValue(NativeHandle handle) : base(handle)
    {
        Type = AXValueGetType(handle.Handle);
        if (Type == AXValueType.ValueIllegal) return;

        var buffer = Marshal.AllocHGlobal(Type switch
        {
            AXValueType.CGPoint => Unsafe.SizeOf<CGPoint>(),
            AXValueType.CGSize => Unsafe.SizeOf<CGSize>(),
            AXValueType.CGRect => Unsafe.SizeOf<CGRect>(),
            AXValueType.CFRange => Unsafe.SizeOf<CFRange>(),
            AXValueType.AXError => Unsafe.SizeOf<AXError>(),
            _ => 0
        });
        try
        {
            if (!AXValueGetValue(handle.Handle, Type, buffer)) return;

            switch (Type)
            {
                case AXValueType.CGPoint:
                {
                    Point = Marshal.PtrToStructure<CGPoint>(buffer);
                    break;
                }
                case AXValueType.CGSize:
                {
                    Size = Marshal.PtrToStructure<CGSize>(buffer);
                    break;
                }
                case AXValueType.CGRect:
                {
                    Rect = Marshal.PtrToStructure<CGRect>(buffer);
                    break;
                }
                case AXValueType.CFRange:
                {
                    Range = Marshal.PtrToStructure<CFRange>(buffer);
                    break;
                }
                case AXValueType.AXError:
                {
                    Error = (AXError)Marshal.ReadInt32(buffer);
                    break;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static AXValueType GetValueType(nint value) => AXValueGetType(value);

    internal static unsafe bool TryGetPoint(nint value, out CGPoint result)
    {
        result = default;
        fixed (CGPoint* pointer = &result)
        {
            return AXValueGetValue(value, AXValueType.CGPoint, (nint)pointer);
        }
    }

    internal static unsafe bool TryGetSize(nint value, out CGSize result)
    {
        result = default;
        fixed (CGSize* pointer = &result)
        {
            return AXValueGetValue(value, AXValueType.CGSize, (nint)pointer);
        }
    }

    internal static unsafe bool TryGetError(nint value, out AXError result)
    {
        result = default;
        fixed (AXError* pointer = &result)
        {
            return AXValueGetValue(value, AXValueType.AXError, (nint)pointer);
        }
    }

    private const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [LibraryImport(AppServices)]
    private static partial nuint AXValueGetTypeID();

    [LibraryImport(AppServices)]
    private static partial AXValueType AXValueGetType(nint value);

    [LibraryImport(AppServices)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AXValueGetValue(nint value, AXValueType theType, nint valuePtr);
}
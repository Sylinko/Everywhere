using System.Runtime.InteropServices;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Copies borrowed Core Foundation scalar values into managed values without creating shared NSObject wrappers.
/// </summary>
internal static partial class AXScalarValueReader
{
    internal static AXError GetSlotError(nint value)
    {
        if (value == 0 || CFInterop.CFGetTypeID(value) == CFNullGetTypeID())
        {
            return AXError.NoValue;
        }

        if (CFInterop.CFGetTypeID(value) != AXValue.TypeId || AXValue.GetValueType(value) != AXValueType.AXError)
        {
            return AXError.Success;
        }

        return AXValue.TryGetError(value, out var error) ? error : AXError.Failure;
    }

    internal static bool TryReadString(nint value, out string? result)
    {
        result = null;
        if (value == 0 || CFInterop.CFGetTypeID(value) != CFStringGetTypeID())
        {
            return false;
        }

        var length = CFStringGetLength(value);
        if (length is < 0 or > int.MaxValue)
        {
            return false;
        }

        result = CopyString(value, (int)length);
        return true;
    }

    internal static bool TryReadBoolean(nint value, out bool result)
    {
        result = false;
        if (value == 0 || CFInterop.CFGetTypeID(value) != CFBooleanGetTypeID())
        {
            return false;
        }

        result = CFBooleanGetValue(value) != 0;
        return true;
    }

    internal static bool TryReadInt64(nint value, out long result)
    {
        result = 0;
        return value != 0 &&
            CFInterop.CFGetTypeID(value) == CFNumberGetTypeID() &&
            CFNumberGetValue(value, CFNumberType.SInt64, out result) != 0;
    }

    internal static bool TryReadPoint(nint value, out CGPoint result)
    {
        result = default;
        return value != 0 &&
            CFInterop.CFGetTypeID(value) == AXValue.TypeId &&
            AXValue.GetValueType(value) == AXValueType.CGPoint &&
            AXValue.TryGetPoint(value, out result);
    }

    internal static bool TryReadSize(nint value, out CGSize result)
    {
        result = default;
        return value != 0 &&
            CFInterop.CFGetTypeID(value) == AXValue.TypeId &&
            AXValue.GetValueType(value) == AXValueType.CGSize &&
            AXValue.TryGetSize(value, out result);
    }

    internal static string? ReadDescription(nint value)
    {
        if (TryReadString(value, out var result))
        {
            return result;
        }

        var description = value == 0 ? 0 : CFCopyDescription(value);
        if (description == 0)
        {
            return null;
        }

        try
        {
            return TryReadString(description, out result) ? result : null;
        }
        finally
        {
            CFInterop.CFRelease(description);
        }
    }

    private static unsafe string CopyString(nint value, int length)
    {
        if (length == 0)
        {
            return string.Empty;
        }

        return string.Create(
            length,
            value,
            static (characters, stringValue) =>
            {
                fixed (char* charactersPointer = characters)
                {
                    CFStringGetCharacters(stringValue, new CFRangeValue(0, characters.Length), charactersPointer);
                }
            });
    }

    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNullGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFStringGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial nint CFStringGetLength(nint value);

    [LibraryImport(CoreFoundation)]
    private static unsafe partial void CFStringGetCharacters(nint value, CFRangeValue range, char* buffer);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFBooleanGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial byte CFBooleanGetValue(nint value);

    [LibraryImport(CoreFoundation)]
    private static partial nuint CFNumberGetTypeID();

    [LibraryImport(CoreFoundation)]
    private static partial byte CFNumberGetValue(nint number, CFNumberType numberType, out long value);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFCopyDescription(nint value);

    [Serializable]
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CFRangeValue(nint Location, nint Length);

    private enum CFNumberType
    {
        SInt64 = 4,
    }
}
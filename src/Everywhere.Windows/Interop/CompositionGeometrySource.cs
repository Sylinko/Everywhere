using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics;
using Windows.UI.Composition;
using Everywhere.Utilities;
using Vortice.Direct2D1;
using WinRT;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Exposes a device-independent Direct2D geometry to Composition without allocating a drawing surface.
/// </summary>
[GeneratedComClass]
internal sealed partial class CompositionGeometrySource(ID2D1Geometry geometry) : IGeometrySource2DAbi, IGeometrySource2DInterop, IDisposable
{
    private ID2D1Geometry? _geometry = geometry;

    public unsafe CompositionPath CreatePath()
    {
        var pointer = ComInterfaceMarshaller<IGeometrySource2DAbi>.ConvertToUnmanaged(this);
        try
        {
            var source = MarshalInterface<IGeometrySource2D>.FromAbi((nint)pointer);
            return new CompositionPath(source);
        }
        finally
        {
            ComInterfaceMarshaller<IGeometrySource2DAbi>.Free(pointer);
        }
    }

    public void Dispose() => DisposeHelper.DisposeToDefault(ref _geometry);

    public int GetGeometry(out nint value)
    {
        value = _geometry?.NativePointer ?? 0;
        if (value == 0) return unchecked((int)0x80000013); // RO_E_CLOSED
        Marshal.AddRef(value);
        return 0;
    }

    public int TryGetGeometryUsingFactory(nint factory, out nint value)
    {
        // Composition may ask for a geometry from its own factory. Returning null lets it
        // consume the original device-independent geometry through GetGeometry instead.
        value = 0;
        return 0;
    }

    public void GetIids(out uint count, out nint values)
    {
        count = 1;
        values = Marshal.AllocCoTaskMem(Marshal.SizeOf<Guid>());
        Marshal.StructureToPtr(typeof(IGeometrySource2DAbi).GUID, values, false);
    }

    public void GetRuntimeClassName(out nint value) => value = 0;

    public void GetTrustLevel(out int value) => value = 0;
}

// The native IGeometrySource2D marker inherits IInspectable. Explicit ABI declarations
// keep its CCW separate from C#/WinRT's projection and support the native geometry interop IID.
[GeneratedComInterface]
[Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90")]
internal partial interface IGeometryInspectable
{
    void GetIids(out uint count, out nint values);
    void GetRuntimeClassName(out nint value);
    void GetTrustLevel(out int value);
}

[GeneratedComInterface]
[Guid("CAFF7902-670C-4181-A624-DA977203B845")]
internal partial interface IGeometrySource2DAbi : IGeometryInspectable;

[GeneratedComInterface]
[Guid("0657AF73-53FD-47CF-84FF-C8492D2A80A3")]
internal partial interface IGeometrySource2DInterop
{
    [PreserveSig]
    int GetGeometry(out nint value);

    [PreserveSig]
    int TryGetGeometryUsingFactory(nint factory, out nint value);
}
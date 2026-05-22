using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Everywhere.Windows.Interop;

[GeneratedComInterface]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal unsafe partial interface IMemoryBufferByteAccess
{
    [PreserveSig]
    int GetBuffer(out byte* value, out uint capacity);
}
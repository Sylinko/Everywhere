using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Everywhere.Windows.Interop;

[GeneratedComInterface]
[Guid("29E691FA-4567-4DCA-B319-D0F207EB6807")]
internal partial interface ICompositorDesktopInterop
{
    nint CreateDesktopWindowTarget(nint hwndTarget, [MarshalAs(UnmanagedType.Bool)] bool isTopmost);
}
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;

namespace Everywhere.Windows.Interop.TextInput;

/// <summary>
/// Declares the used prefix of the current-thread input capture ABI returned by the activation factory.
/// Its interface pointer is obtained from Current, not by querying an assumed IID.
/// </summary>
// Interface and vtable fields are supplied by native code, never assigned by managed code.
#pragma warning disable CS0649
internal unsafe struct ITextInputCapture
{
    private Vtbl* _vtable;

    public HRESULT RequestCapture()
    {
        return _vtable->RequestCapture((ITextInputCapture*)Unsafe.AsPointer(ref this));
    }

    public HRESULT ReleaseCapture()
    {
        return _vtable->ReleaseCapture((ITextInputCapture*)Unsafe.AsPointer(ref this));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vtbl
    {
        private IInspectable.Vtbl _inspectable;
        private delegate* unmanaged[Stdcall]<ITextInputCapture*, HRESULT> _requestCapture;
        private delegate* unmanaged[Stdcall]<ITextInputCapture*, HRESULT> _releaseCapture;

        public HRESULT RequestCapture(ITextInputCapture* capture) => _requestCapture(capture);

        public HRESULT ReleaseCapture(ITextInputCapture* capture) => _releaseCapture(capture);
    }
}
#pragma warning restore CS0649
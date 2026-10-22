using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;

namespace Everywhere.Windows.Interop.TextInput;

/// <summary>
/// Declares the used prefix of WindowsUdk.UI.Input.Text.TextInputCapture's activation factory ABI.
/// </summary>
// Interface and vtable fields are supplied by native code, never assigned by managed code.
#pragma warning disable CS0649
[Guid("EFB97A91-0252-56E1-96F7-7D444006F569")]
internal unsafe struct ITextInputCaptureStatics : IComIID
{
    private static readonly Guid Iid = typeof(ITextInputCaptureStatics).GUID;

    static ref readonly Guid IComIID.Guid => ref Iid;

    private Vtbl* _vtable;

    public HRESULT GetCurrent(out ITextInputCapture* capture)
    {
        fixed (ITextInputCapture** result = &capture)
        {
            return _vtable->GetCurrent((ITextInputCaptureStatics*)Unsafe.AsPointer(ref this), result);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vtbl
    {
        private IInspectable.Vtbl _inspectable;
        private delegate* unmanaged[Stdcall]<ITextInputCaptureStatics*, ITextInputCapture**, HRESULT> _getCurrent;

        public HRESULT GetCurrent(ITextInputCaptureStatics* factory, ITextInputCapture** capture)
        {
            return _getCurrent(factory, capture);
        }
    }
}
#pragma warning restore CS0649
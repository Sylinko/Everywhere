using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;

namespace Everywhere.Windows.Interop.TextInput;

/// <summary>
/// Declares the used prefix of the private CoreWindow input consumer ABI.
/// </summary>
// Interface and vtable fields are supplied by native code, never assigned by managed code.
#pragma warning disable CS0649
[Guid("A9D00AB3-2FEF-41A0-B0AD-4B2129EA2663")]
internal unsafe struct ITextInputConsumer : IComIID
{
    private static readonly Guid Iid = typeof(ITextInputConsumer).GUID;

    static ref readonly Guid IComIID.Guid => ref Iid;

    private Vtbl* _vtable;

    public HRESULT SetTextInputProducer(IUnknown* producer)
    {
        return _vtable->SetTextInputProducer((ITextInputConsumer*)Unsafe.AsPointer(ref this), producer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Vtbl
    {
        private IInspectable.Vtbl _inspectable;
        private delegate* unmanaged[Stdcall]<ITextInputConsumer*, IUnknown**, HRESULT> _getTextInputProducer;
        private delegate* unmanaged[Stdcall]<ITextInputConsumer*, IUnknown*, HRESULT> _setTextInputProducer;

        public HRESULT SetTextInputProducer(ITextInputConsumer* consumer, IUnknown* producer)
        {
            return _setTextInputProducer(consumer, producer);
        }
    }
}
#pragma warning restore CS0649
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.System.WinRT;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Owns the UI thread's hidden CoreWindow and its private desktop input producer.
/// No native pointers or input text cross the UI process boundary.
/// </summary>
internal sealed unsafe partial class CoreWindowInputHost : IDisposable
{
    public CoreWindow Window { get; }

    public nint Handle { get; }

    public WindowsWindowInputCapture? Capture { get; set; }

    private nint _core;
    private nint _producer;
    private nint _capture;
    private bool _isRuntimeInitialized;

    public CoreWindowInputHost()
    {
        Dispatcher.UIThread.VerifyAccess();
        try
        {
            // CoreWindow, its producer, and CoreTextServicesManager must share this UI STA.
            // RO_INIT_SINGLETHREADED also pairs with RoUninitialize on this same thread.
            PInvoke.RoInitialize(0).ThrowOnFailure();
            _isRuntimeInitialized = true;

            // Private desktop CoreWindow bootstrap observed in the system Emoji input route.
            // Windows.UI.dll ordinal 1500 accepts type 6 for a desktop-hosted CoreWindow;
            // this is not the public UWP application/view startup path. Both ordinal exports
            // and the private interface layouts below were verified on Windows 11 26100.9711,
            // not established as public contracts or verified on the minimum Windows 10 target.
            // TryPrepareInput reports missing exports/interfaces without activating the overlay.
            var coreIid = new Guid("79B9D5F2-879E-4B89-B798-79E47598030C");
            Marshal.ThrowExceptionForHR(CreateCoreWindow(6, "Everywhere input host", 0, 0, 1, 1, 0, 0, ref coreIid, out _core));
            Window = CoreWindow.FromAbi(_core);

            // ICoreWindowInterop exposes the host HWND at IUnknown slot 3. It is a distinct,
            // hidden input owner: the visible Avalonia HWND must never take native focus.
            var interopIid = new Guid("45D64A29-A63E-4CB6-B498-5781D298CB4F");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_core, in interopIid, out var interop));
            try
            {
                var hwnd = nint.Zero;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(interop, 3))(interop, &hwnd));
                Handle = hwnd;
            }
            finally
            {
                Marshal.Release(interop);
            }

            TextInputNative.MakeNonActivating(Handle);
            // ITextInputConsumer alone is insufficient. The separate Core.TextInput export
            // creates its producer; consumer slot 7 connects that producer to the CoreWindow.
            // Retain the producer for the host lifetime, not just for a capture lease.
            var consumerIid = new Guid("a9d00ab3-2fef-41a0-b0ad-4b2129ea2663");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(_core, in consumerIid, out var consumer));
            try
            {
                Marshal.ThrowExceptionForHR(CreateProducer(consumer, out _producer));
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(consumer, 7))(consumer, _producer));
            }
            finally
            {
                Marshal.Release(consumer);
            }

            // TextInputCapture's statics expose Current at IInspectable slot 6. The returned
            // current-thread capture object provides RequestCapture/ReleaseCapture at slots 6/7.
            // Native host creation is preparation only; do not call RequestCapture here.
            const string classId = "WindowsUdk.UI.Input.Text.TextInputCapture";
            PInvoke.WindowsCreateString(classId, (uint)classId.Length, out var className).ThrowOnFailure();
            using (className)
            {
                var iid = new Guid("efb97a91-0252-56e1-96f7-7d444006f569");
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(new HSTRING(className.DangerousGetHandle()), ref iid, out var factory));
                try
                {
                    var capture = nint.Zero;
                    Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(factory, 6))(factory, &capture));
                    _capture = capture;
                }
                finally
                {
                    Marshal.Release(factory);
                }
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void RequestCapture()
    {
        Dispatcher.UIThread.VerifyAccess();
        // S_OK and GetFocus == host HWND are useful sanity checks, but alone did not prove
        // input delegation in the failed single-stage probes. Preserve the two-stage caller.
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_capture, 6))(_capture));
    }

    public void ReleaseCapture()
    {
        Dispatcher.UIThread.VerifyAccess();
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, int>)Slot(_capture, 7))(_capture));
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        Capture?.Dispose();
        if (_capture != 0)
        {
            Marshal.Release(_capture);
            _capture = 0;
        }

        if (_core != 0)
        {
            Window.Close();
        }

        if (_producer != 0)
        {
            Marshal.Release(_producer);
            _producer = 0;
        }

        if (_core != 0)
        {
            Marshal.Release(_core);
            _core = 0;
        }

        if (_isRuntimeInitialized)
        {
            PInvoke.RoUninitialize();
            _isRuntimeInitialized = false;
        }
    }

    private static nint Slot(nint instance, int slot) => (*(nint**)instance)[slot];

    [LibraryImport("Windows.UI.dll", EntryPoint = "#1500", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int CreateCoreWindow(
        int type,
        string title,
        int x,
        int y,
        uint width,
        uint height,
        uint flags,
        nint owner,
        ref Guid iid,
        out nint result);

    [LibraryImport("Windows.UI.Core.TextInput.dll", EntryPoint = "#1500")]
    private static partial int CreateProducer(nint consumer, out nint producer);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint name, ref Guid iid, out nint factory);
}
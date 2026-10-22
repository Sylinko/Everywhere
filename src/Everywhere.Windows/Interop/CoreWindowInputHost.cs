using Avalonia.Threading;
using Everywhere.Windows.Interop.TextInput;
using Windows.UI.Core;
using Windows.Win32;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Owns the UI thread's hidden CoreWindow and its private desktop input producer.
/// No native pointers or input text cross the UI process boundary.
/// </summary>
internal sealed class CoreWindowInputHost : IDisposable
{
    public CoreWindow Window { get; }

    public nint Handle { get; }

    public WindowsWindowInputCapture? Capture { get; set; }

    private nint _pCoreWindow;
    private nint _pInputProducer;
    private nint _pCapture;
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

            // Native declarations live in the Interop assembly. The host retains their owned
            // references so cleanup still occurs in this UI STA before RoUninitialize.
            _pCoreWindow = DesktopTextInputInterop.CreateCoreWindow();
            Window = CoreWindow.FromAbi(_pCoreWindow);

            // This HWND is a distinct, hidden input owner. The visible Avalonia HWND must
            // never take native focus while using delegated input.
            Handle = DesktopTextInputInterop.GetWindowHandle(_pCoreWindow);
            TextInputNative.MakeNonActivating(Handle);
            _pInputProducer = DesktopTextInputInterop.CreateInputProducer(_pCoreWindow);

            // Obtaining Current is preparation only. RequestCapture belongs to the later,
            // explicit switch to editing, after the passive toolbar has appeared.
            _pCapture = DesktopTextInputInterop.GetCurrentCapture();
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
        DesktopTextInputInterop.RequestCapture(_pCapture);
    }

    public void ReleaseCapture()
    {
        Dispatcher.UIThread.VerifyAccess();
        DesktopTextInputInterop.ReleaseCapture(_pCapture);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        Capture?.Dispose();
        if (_pCapture != 0)
        {
            DesktopTextInputInterop.ReleaseReference(_pCapture);
            _pCapture = 0;
        }

        if (_pCoreWindow != 0)
        {
            Window.Close();
        }

        if (_pInputProducer != 0)
        {
            DesktopTextInputInterop.ReleaseReference(_pInputProducer);
            _pInputProducer = 0;
        }

        if (_pCoreWindow != 0)
        {
            DesktopTextInputInterop.ReleaseReference(_pCoreWindow);
            _pCoreWindow = 0;
        }

        if (_isRuntimeInitialized)
        {
            PInvoke.RoUninitialize();
            _isRuntimeInitialized = false;
        }
    }
}
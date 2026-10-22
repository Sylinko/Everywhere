using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.WinRT;

namespace Everywhere.Windows.Interop.TextInput;

/// <summary>
/// Provides the native desktop CoreWindow input ABI without exposing CsWin32's assembly-local types.
/// </summary>
/// <remarks>
/// The caller owns every returned COM reference and must release it on its owning UI STA.
/// These methods neither initialize WinRT nor acquire input implicitly. The private exports and
/// interface layouts were verified on Windows 10 build 19041 and Windows 11 build 26100.9711.
/// </remarks>
public static unsafe partial class DesktopTextInputInterop
{
    private static readonly Guid CoreWindowIid = new("79B9D5F2-879E-4B89-B798-79E47598030C");

    /// <summary>
    /// Creates an owned desktop CoreWindow reference without activating it or requesting input.
    /// </summary>
    public static nint CreateCoreWindow()
    {
        // Type 6 selects the private desktop bootstrap, not the public UWP view startup path.
        var iid = CoreWindowIid;
        ((HRESULT)CreateCoreWindow(6, "Everywhere input host", 0, 0, 1, 1, 0, 0, in iid, out var coreWindow)).ThrowOnFailure();
        return coreWindow;
    }

    /// <summary>
    /// Gets the HWND of a borrowed CoreWindow reference through the SDK-defined interop interface.
    /// </summary>
    public static nint GetWindowHandle(nint coreWindow)
    {
        ((IUnknown*)coreWindow)->QueryInterface(out ICoreWindowInterop* interop).ThrowOnFailure();
        try
        {
            return interop->WindowHandle;
        }
        finally
        {
            interop->Release();
        }
    }

    /// <summary>
    /// Connects a new input producer to a borrowed CoreWindow and returns the producer's owned reference.
    /// </summary>
    public static nint CreateInputProducer(nint coreWindow)
    {
        ((IUnknown*)coreWindow)->QueryInterface(out ITextInputConsumer* consumer).ThrowOnFailure();
        try
        {
            // The consumer alone cannot receive delegated input. Keep the connected producer alive
            // for the CoreWindow lifetime, not merely for one RequestCapture/ReleaseCapture pair.
            ((HRESULT)CreateInputProducer(consumer, out var producerReference)).ThrowOnFailure();
            var producer = (IUnknown*)producerReference;
            try
            {
                consumer->SetTextInputProducer(producer).ThrowOnFailure();
                return (nint)producer;
            }
            catch
            {
                producer->Release();
                throw;
            }
        }
        finally
        {
            ((IUnknown*)consumer)->Release();
        }
    }

    /// <summary>
    /// Gets an owned reference to the calling thread's input capture object without requesting capture.
    /// </summary>
    public static nint GetCurrentCapture()
    {
        const string classId = "WindowsUdk.UI.Input.Text.TextInputCapture";
        PInvoke.WindowsCreateString(classId, (uint)classId.Length, out var className).ThrowOnFailure();
        using (className)
        {
            PInvoke.RoGetActivationFactory(className, out ITextInputCaptureStatics* factory).ThrowOnFailure();
            try
            {
                factory->GetCurrent(out var capture).ThrowOnFailure();
                return (nint)capture;
            }
            finally
            {
                ((IUnknown*)factory)->Release();
            }
        }
    }

    /// <summary>
    /// Requests input using a borrowed, previously prepared capture reference on its owning UI STA.
    /// </summary>
    public static void RequestCapture(nint capture)
    {
        ((ITextInputCapture*)capture)->RequestCapture().ThrowOnFailure();
    }

    /// <summary>
    /// Ends input capture without releasing the caller's COM reference.
    /// </summary>
    public static void ReleaseCapture(nint capture)
    {
        ((ITextInputCapture*)capture)->ReleaseCapture().ThrowOnFailure();
    }

    /// <summary>
    /// Releases one owned COM reference on its owning thread. The pointer must not be null.
    /// </summary>
    public static void ReleaseReference(nint reference)
    {
        ((IUnknown*)reference)->Release();
    }

    // Keep imports primitive: LibraryImport cannot consume types emitted by another source
    // generator in the same compilation. Convert HRESULTs after the native call instead.
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
        in Guid iid,
        out nint result);

    [LibraryImport("Windows.UI.Core.TextInput.dll", EntryPoint = "#1500")]
    private static partial int CreateInputProducer(ITextInputConsumer* consumer, out nint producer);
}
using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.WinRT;
using Windows.Win32.UI.WindowsAndMessaging;
using Avalonia;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.Utilities;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using WinRT;
using Visual = Windows.UI.Composition.Visual;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Owns one capture's STA message loop and mapped staging buffer until the consumer disposes the result.
/// </summary>
public sealed partial class Direct3D11ScreenCapture : IVisualElementCapture
{
    /// <inheritdoc />
    public PixelFormat Format => PixelFormat.Bgra8888;
    /// <inheritdoc />
    public AlphaFormat AlphaFormat => AlphaFormat.Premul;
    /// <inheritdoc />
    public nint Data { get; private set; }
    /// <inheritdoc />
    public PixelSize Size { get; private set; }
    /// <inheritdoc />
    public PixelRect Bounds { get; private set; }
    /// <inheritdoc />
    public int Stride { get; private set; }

    private ID3D11Device? _d3D11Device;
    private IDirect3DDevice? _direct3DDevice;
    private ID2D1Device? _d2DDevice;
    private IDCompositionDesktopDevice? _dCompositionDesktopDevice;
    private InvisibleWindow? _hostWindow;
    private nint _hThumbnailId;
    private IDCompositionVisual2? _dCompositionVisual;
    private Direct3D11CaptureFramePool? _framePool;
    private GraphicsCaptureSession? _session;

    private ID3D11Texture2D? _stagingTexture;
    private readonly TaskCompletionSource<DispatcherQueue?> _dispatcherReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _captureReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Direct3D11CaptureFrame? _pendingFrame;
    // The request flag is the only mutable state shared with the consumer; all native work stays on the STA.
    private int _isDisposeRequested;
    private bool _hasReceivedFrame;

    private Direct3D11ScreenCapture(nint sourceHWnd, PixelPoint sourceOrigin, PixelRect relativeRect)
    {
        // Independent loops keep concurrent captures and GPU readback waits isolated without copying an extra frame.
        // Consider a shared loop only if measured thread startup or retained-result costs justify serializing readback.
        _thread = new Thread(() => RunCaptureLoop(sourceHWnd, sourceOrigin, relativeRect))
        {
            IsBackground = true,
            Name = "Everywhere.D3D11Capture",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    // https://blog.adeltax.com/dwm-thumbnails-but-with-idcompositionvisual/
    // https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247
    private void InitializeCapture(nint sourceHWnd, PixelPoint sourceOrigin, PixelRect relativeRect)
    {
        DwmpQueryWindowThumbnailSourceSize((HWND)sourceHWnd, false, out var srcSize).ThrowOnFailure();
        if (srcSize.Width == 0 || srcSize.Height == 0)
        {
            throw new InvalidOperationException("Failed to query thumbnail source size.");
        }

        // Clip against the actual thumbnail surface, not the desktop or the queried TopLevel bounds.
        // DWM may expose content for an offscreen or minimized window.
        relativeRect = relativeRect.Intersect(new PixelRect(0, 0, srcSize.Width, srcSize.Height));
        if (relativeRect.Width <= 0 || relativeRect.Height <= 0)
            throw new InvalidOperationException("The requested region does not intersect the DWM source surface.");

        Bounds = new PixelRect(
            checked(sourceOrigin.X + relativeRect.X),
            checked(sourceOrigin.Y + relativeRect.Y),
            relativeRect.Width,
            relativeRect.Height);

        // Create D3D and DXGI device
        _d3D11Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using var dxgiDevice = _d3D11Device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pD3D11Device));
        try
        {
            _direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pD3D11Device);
        }
        finally
        {
            // FromAbi retains its own reference; release the reference returned by the native factory.
            MarshalInterface<IDirect3DDevice>.DisposeAbi(pD3D11Device);
        }
        _d2DDevice = D2D1.D2D1CreateDevice(dxgiDevice);

        // Create the composition device via InteropCompositor
        // Request IDCompositionDesktopDevice (standard IID, compatible with Win10 19041+)
        // Previously used a private/internal IID (e7894c70-...) that caused vtable mismatch on Win10
        var interopCompositorFactory = Compositor.As<IInteropCompositorFactoryPartner>();
        var pInteropCompositor = interopCompositorFactory.CreateInteropCompositor(
            _d2DDevice.NativePointer,
            0,
            typeof(IDCompositionDevice2).GUID);
        if (pInteropCompositor == 0)
        {
            throw new InvalidOperationException("Failed to create interop compositor.");
        }

        using var interopCompositor = new IDCompositionDevice2(pInteropCompositor);
        _hostWindow = new InvisibleWindow();

        // Create the shared thumbnail visual
        var thumbProperties = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmThumbnailPropertyFlags.RectDestination | DwmThumbnailPropertyFlags.Visible,
            rcDestination = new RECT(0, 0, srcSize.Width, srcSize.Height),
            fVisible = true,
        };
        DwmpCreateSharedThumbnailVisual(
            _hostWindow.HWnd,
            (HWND)sourceHWnd,
            2, // Undocumented flag
            ref thumbProperties,
            pInteropCompositor,
            out var pDCompositionVisual,
            out _hThumbnailId).ThrowOnFailure();
        _dCompositionVisual = new IDCompositionVisual2(pDCompositionVisual);

        // Transform and crop the visual using relativeRect
        _dCompositionDesktopDevice = interopCompositor.QueryInterface<IDCompositionDesktopDevice>();
        using var containerVisual = _dCompositionDesktopDevice.CreateVisual();
        containerVisual.AddVisual(_dCompositionVisual, true, null);

        // Create a transform matrix for translation
        using var transform = _dCompositionDesktopDevice.CreateMatrixTransform();
        var outputSize = IVisualElementCapture.LimitOutputSize(relativeRect.Size);
        var matrix =
            Matrix3x2.CreateTranslation(-relativeRect.X, -relativeRect.Y) *
            Matrix3x2.CreateScale((float)outputSize.Width / relativeRect.Width, (float)outputSize.Height / relativeRect.Height);
        transform.SetMatrix(ref matrix);
        _dCompositionVisual.SetTransform(transform);

        // Set the clip region
        containerVisual.SetClip(new RawRectF(0, 0, outputSize.Width, outputSize.Height));

        // This is a projection of a DComposition-owned visual. Its WinRT Close operation is not supported.
        var visual = Visual.FromAbi(containerVisual.NativePointer);
        visual.Size = new Vector2(outputSize.Width, outputSize.Height);

        _framePool = Direct3D11CaptureFramePool.Create(
            _direct3DDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized,
            2, // Use a buffer of 2 to avoid capture lag
            new SizeInt32(outputSize.Width, outputSize.Height));

        var item = GraphicsCaptureItem.CreateFromVisual(visual);
        _session = _framePool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
    }

    private unsafe void RunCaptureLoop(nint sourceHWnd, PixelPoint sourceOrigin, PixelRect relativeRect)
    {
        DispatcherQueueController? controller = null;
        Exception? failure = null;
        try
        {
            PInvoke.CreateDispatcherQueueController(
                new DispatcherQueueOptions
                {
                    apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_NONE,
                    threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                    dwSize = (uint)Unsafe.SizeOf<DispatcherQueueOptions>(),
                },
                out controller).ThrowOnFailure();
            _dispatcherReady.SetResult(controller.DispatcherQueue);

            if (Volatile.Read(ref _isDisposeRequested) == 0)
            {
                InitializeCapture(sourceHWnd, sourceOrigin, relativeRect);
                if (_framePool is null || _session is null || _dCompositionDesktopDevice is null)
                    throw new InvalidOperationException("Capture session is not properly initialized.");

                _framePool.FrameArrived += HandleFrameArrived;
                _session.StartCapture();
                _dCompositionDesktopDevice.Commit();
            }

            PumpMessages();
        }
        catch (Exception exception)
        {
            failure = exception;
            _dispatcherReady.TrySetResult(null);
            _captureReady.TrySetException(exception);
        }
        finally
        {
            try
            {
                DisposeCore();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            try
            {
                if (controller is not null)
                {
                    // A current-thread queue must keep pumping until shutdown completes, before the STA exits.
                    var threadId = PInvoke.GetCurrentThreadId();
                    var shutdown = controller.ShutdownQueueAsync();
                    shutdown.Completed = (_, _) => PInvoke.PostThreadMessage(threadId, (uint)WINDOW_MESSAGE.WM_QUIT, default, default);
                    PumpMessages();
                    shutdown.GetResults();
                }
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }

            if (failure is null)
                _threadExited.TrySetResult();
            else
            {
                _captureReady.TrySetException(failure);
                _threadExited.TrySetException(failure);
            }
        }
    }

    private static unsafe void PumpMessages()
    {
        while (true)
        {
            var message = default(MSG);
            var result = PInvoke.GetMessage(&message, HWND.Null, 0, 0);
            if (result < 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "The capture message loop failed.");
            if (result == 0) return;
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    private void HandleFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        Debug.Assert(Thread.CurrentThread == _thread);
        if (_hasReceivedFrame || Volatile.Read(ref _isDisposeRequested) != 0) return;
        try
        {
            var frame = sender.TryGetNextFrame();
            if (frame is null) return;
            _pendingFrame = frame;
            _hasReceivedFrame = true;

            // Always queue: Map and capture shutdown must run after FrameArrived has returned.
            // Use our owned queue: the frame pool's DispatcherQueue property can return null even with Create.
            var queue = _dispatcherReady.Task.GetAwaiter().GetResult() ??
                throw new InvalidOperationException("The capture dispatcher is unavailable.");
            if (!queue.TryEnqueue(ProcessFrame))
                throw new InvalidOperationException("Failed to dispatch captured-frame processing.");
        }
        catch (Exception exception)
        {
            _captureReady.TrySetException(exception);
        }
    }

    private void ProcessFrame()
    {
        Debug.Assert(Thread.CurrentThread == _thread);
        if (Volatile.Read(ref _isDisposeRequested) != 0) return;
        try
        {
            if (_pendingFrame is not { } frame || _d3D11Device is not { } device)
                throw new InvalidOperationException("The captured frame is no longer available.");

            try
            {
                var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
                using var sourceTexture = new ID3D11Texture2D(access.GetInterface(typeof(ID3D11Texture2D).GUID));
                var desc = sourceTexture.Description;
                _stagingTexture = device.CreateTexture2D(
                    new Texture2DDescription
                    {
                        Width = desc.Width,
                        Height = desc.Height,
                        ArraySize = 1,
                        BindFlags = BindFlags.None,
                        Usage = ResourceUsage.Staging,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        Format = desc.Format,
                        MipLevels = 1,
                        SampleDescription = new SampleDescription(1, 0),
                        MiscFlags = ResourceOptionFlags.None,
                    });

                var context = device.ImmediateContext;
                context.CopyResource(_stagingTexture, sourceTexture);
                var mapBox = context.Map(_stagingTexture, 0);
                if (mapBox.DataPointer == 0) throw new InvalidOperationException("Failed to map staging texture.");

                // Record the mapping before checked conversions so any failure still unmaps it during cleanup.
                Data = mapBox.DataPointer;
                Stride = checked((int)mapBox.RowPitch);
                Size = new PixelSize(checked((int)desc.Width), checked((int)desc.Height));
            }
            finally
            {
                _pendingFrame = null;
                frame.Dispose();
            }

            StopCapture();
            _captureReady.TrySetResult();
        }
        catch (Exception exception)
        {
            _captureReady.TrySetException(exception);
        }
    }

    private void StopCapture()
    {
        // Drop producer resources before publishing the result. The staging texture and D3D device stay mapped
        // while the RPC stream reads its chunks, and are released by DisposeCore on this same thread.
        if (_framePool is not null) _framePool.FrameArrived -= HandleFrameArrived;

        DisposeHelper.DisposeToDefault(ref _pendingFrame);
        DisposeHelper.DisposeToDefault(ref _session);
        DisposeHelper.DisposeToDefault(ref _framePool);

        if (_hThumbnailId != 0)
        {
            DwmUnregisterThumbnail(_hThumbnailId);
            _hThumbnailId = 0;
        }

        DisposeHelper.DisposeToDefault(ref _dCompositionVisual);
        DisposeHelper.DisposeToDefault(ref _dCompositionDesktopDevice);
        DisposeHelper.DisposeToDefault(ref _d2DDevice);
        DisposeHelper.DisposeToDefault(ref _direct3DDevice);
        DisposeHelper.DisposeToDefault(ref _hostWindow);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposeRequested, 1) == 0)
        {
            var queue = _dispatcherReady.Task.GetAwaiter().GetResult();
            queue?.TryEnqueue(() => PInvoke.PostThreadMessage(PInvoke.GetCurrentThreadId(), (uint)WINDOW_MESSAGE.WM_QUIT, default, default));
        }

        // Callers own the result exclusively and must finish reading before disposal. Waiting also guarantees
        // that canceled or failed captures leave no live window or dispatcher thread behind.
        try
        {
            _threadExited.Task.GetAwaiter().GetResult();
        }
        finally
        {
            _thread.Join();
        }
    }

    private void DisposeCore()
    {
        try
        {
            StopCapture();
        }
        finally
        {
            if (Data != 0 && _d3D11Device is not null && _stagingTexture is not null)
            {
                _d3D11Device.ImmediateContext.Unmap(_stagingTexture, 0);
                Data = 0;
            }

            DisposeHelper.DisposeToDefault(ref _stagingTexture);
            DisposeHelper.DisposeToDefault(ref _d3D11Device);
        }
    }

    /// <summary>
    /// Captures a window-local region on its own STA loop, scaling composition output before readback.
    /// </summary>
    /// <param name="sourceHWnd">The native window providing the DWM surface.</param>
    /// <param name="sourceOrigin">The observed screen-space origin corresponding to the surface origin.</param>
    /// <param name="relativeRect">The requested physical-pixel rectangle relative to that origin.</param>
    /// <param name="cancellationToken">Cancellation for initialization and the frame wait.</param>
    public static async Task<IVisualElementCapture> CaptureAsync(
        nint sourceHWnd,
        PixelPoint sourceOrigin,
        PixelRect relativeRect,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var screenCapture = new Direct3D11ScreenCapture(sourceHWnd, sourceOrigin, relativeRect);
        try
        {
            await screenCapture._captureReady.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ResizedScreenCapture.Limit(screenCapture);
        }
        catch
        {
            screenCapture.Dispose();
            throw;
        }
    }

    [Flags]
    private enum DwmThumbnailPropertyFlags : uint
    {
        RectDestination = 0x00000001,
        Visible = 0x00000008,
    }

    // ReSharper disable InconsistentNaming
    // ReSharper disable NotAccessedField.Local
    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_THUMBNAIL_PROPERTIES
    {
        public DwmThumbnailPropertyFlags dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        public BOOL fVisible;
        public BOOL fSourceClientAreaOnly;
    }
    // ReSharper restore InconsistentNaming
    // ReSharper restore NotAccessedField.Local

    [LibraryImport("d3d11.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmUnregisterThumbnail([In] nint hThumbnailId);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#162")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpQueryWindowThumbnailSourceSize(
        [In] HWND hWndSource,
        [In] BOOL fSourceClientAreaOnly,
        [Out] out SIZE pSize);

    [DllImport("dwmapi.dll", CallingConvention = CallingConvention.Winapi, PreserveSig = true, EntryPoint = "#147")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern HRESULT DwmpCreateSharedThumbnailVisual(
        [In] HWND hWndDestination,
        [In] HWND hWndSource,
        [In] uint thumbnailFlags,
        [In] ref DWM_THUMBNAIL_PROPERTIES thumbnailProperties,
        [In] nint pDCompositionDesktopDevice,
        [Out] out nint pDCompositionVisual,
        [Out] out nint hThumbnailId);

    private sealed class InvisibleWindow : IDisposable
    {
        public HWND HWnd { get; private set; }

        private readonly WNDPROC _wndProc;
        private string? _className;

        public unsafe InvisibleWindow()
        {
            var hInstance = PInvoke.GetModuleHandle(default(PCWSTR));
            _className = "InvisibleDCompHost_" + Guid.NewGuid().ToString("N");
            fixed (char* pClassName = _className)
            {
                var wndClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = _wndProc = WndProc,
                    hInstance = hInstance,
                    lpszClassName = pClassName
                };
                if (PInvoke.RegisterClassEx(wndClass) == 0)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                HWnd = PInvoke.CreateWindowEx(
                    WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                    WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP |
                    WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                    WINDOW_EX_STYLE.WS_EX_LAYERED |
                    WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                    pClassName,
                    pClassName,
                    WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_DISABLED,
                    0,
                    0,
                    1,
                    1,
                    hInstance: hInstance);
            }

            if (HWnd.IsNull)
            {
                var exception = new Win32Exception(Marshal.GetLastWin32Error());
                Dispose();
                throw exception;
            }

            var cloak = 1;
            PInvoke.DwmSetWindowAttribute(HWnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &cloak, sizeof(int));

            PInvoke.ShowWindow(HWnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }

        private static LRESULT WndProc(HWND hWnd, uint msg, WPARAM wParam, LPARAM lParam)
        {
            switch (msg)
            {
                case (uint)WINDOW_MESSAGE.WM_MOUSEACTIVATE:
                    return new LRESULT(3); // MA_NOACTIVATE
                case (uint)WINDOW_MESSAGE.WM_NCHITTEST:
                    // -1 = HTTRANSPARENT
                    return new LRESULT(-1);
                default:
                    return PInvoke.DefWindowProc(hWnd, msg, wParam, lParam);
            }
        }

        public unsafe void Dispose()
        {
            if (!HWnd.IsNull)
            {
                PInvoke.DestroyWindow(HWnd);
                HWnd = HWND.Null;
            }

            if (_className is not null)
            {
                var hInstance = PInvoke.GetModuleHandle(default(PCWSTR));
                fixed (char* pClassName = _className)
                {
                    PInvoke.UnregisterClass(pClassName, hInstance);
                }

                _className = null;
            }

            GC.KeepAlive(_wndProc);
        }
    }
}
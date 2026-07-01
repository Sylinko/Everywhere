using System.ComponentModel;
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
using Avalonia.Threading;
using ShadUI.Extensions;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using WinRT;
using IVisualElement = Everywhere.Interop.IVisualElement;
using Vector = Avalonia.Vector;
using Visual = Windows.UI.Composition.Visual;

namespace Everywhere.Windows.Interop;

public sealed partial class Direct3D11ScreenCapture : IVisualElement.ICapturedBitmapData
{
    public PixelFormat Format => PixelFormat.Bgra8888;
    public AlphaFormat AlphaFormat => AlphaFormat.Premul;
    public nint Data { get; private set; }
    public PixelSize Size { get; private set; }
    public Vector Dpi => new(96, 96);
    public int Stride { get; private set; }

    private readonly ID3D11Device? _d3D11Device;
    private readonly IDirect3DDevice? _direct3DDevice;
    private readonly ID2D1Device? _d2DDevice;
    private readonly IDCompositionDesktopDevice? _dCompositionDesktopDevice;
    private readonly InvisibleWindow? _hostWindow;
    private readonly nint _hThumbnailId;
    private readonly IDCompositionVisual2? _dCompositionVisual;
    private readonly Direct3D11CaptureFramePool? _framePool;
    private readonly GraphicsCaptureSession? _session;

    private ID3D11Texture2D? _stagingTexture;
    private bool _disposed;
    private int _frameReceived;

    private static DispatcherQueueController? _dispatcherQueueController;

    // https://blog.adeltax.com/dwm-thumbnails-but-with-idcompositionvisual/
    // https://gist.github.com/ADeltaX/aea6aac248604d0cb7d423a61b06e247
    private Direct3D11ScreenCapture(nint sourceHWnd, PixelRect relativeRect)
    {
        try
        {
            if (_dispatcherQueueController is null)
            {
                PInvoke.CreateDispatcherQueueController(
                    new DispatcherQueueOptions
                    {
                        apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
                        threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                        dwSize = (uint)Unsafe.SizeOf<DispatcherQueueOptions>()
                    },
                    out _dispatcherQueueController).ThrowOnFailure();
            }

            DwmpQueryWindowThumbnailSourceSize((HWND)sourceHWnd, false, out var srcSize).ThrowOnFailure();
            if (srcSize.Width == 0 || srcSize.Height == 0)
            {
                throw new InvalidOperationException("Failed to query thumbnail source size.");
            }

            // Create D3D and DXGI device
            _d3D11Device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
            using var dxgiDevice = _d3D11Device.QueryInterface<IDXGIDevice>();
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var pD3D11Device));
            _direct3DDevice = MarshalInterface<IDirect3DDevice>.FromAbi(pD3D11Device);
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

            // Create the shared thumbnail visual
            _hostWindow = new InvisibleWindow();
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
            _dCompositionDesktopDevice = ComObject.As<IDCompositionDesktopDevice>(pInteropCompositor);
            using var containerVisual = _dCompositionDesktopDevice.CreateVisual();
            containerVisual.AddVisual(_dCompositionVisual, true, null);

            // Create a transform matrix for translation
            using var transform = _dCompositionDesktopDevice.CreateMatrixTransform();
            var matrix = Matrix3x2.CreateTranslation(-relativeRect.X, -relativeRect.Y);
            transform.SetMatrix(ref matrix);
            _dCompositionVisual.SetTransform(transform);

            // Set the clip region
            containerVisual.SetClip(new RawRectF(0, 0, relativeRect.Width, relativeRect.Height));

            var visual = Visual.FromAbi(containerVisual.NativePointer);
            visual.Size = new Vector2(relativeRect.Width, relativeRect.Height);

            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _direct3DDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2, // Use a buffer of 2 to avoid capture lag
                new SizeInt32(relativeRect.Width, relativeRect.Height));

            var item = GraphicsCaptureItem.CreateFromVisual(visual);
            _session = _framePool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;

            // Do nothing but keep the DispatcherQueueController alive
            GC.KeepAlive(_dispatcherQueueController);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private async Task CaptureFrameAsync(CancellationToken cancellationToken)
    {
        if (_framePool is null || _d3D11Device is null || _session is null || _dCompositionDesktopDevice is null)
            throw new InvalidOperationException("Capture session is not properly initialized.");

        var tcs = new TaskCompletionSource();
        await using var registration = cancellationToken.Register(() => tcs.TrySetCanceled());

        _framePool.FrameArrived += (f, _) =>
        {
            if (_disposed || Interlocked.Exchange(ref _frameReceived, 1) != 0) return;

            using var frame = f.TryGetNextFrame();
            if (frame is null) return;

            try
            {
                // Get the underlying ID3D11Texture2D from the frame's
                var access = CastExtensions.As<IDirect3DDxgiInterfaceAccess>(frame.Surface);

                var textureGuid = typeof(ID3D11Texture2D).GUID;
                var pTexture = access.GetInterface(textureGuid);
                using var sourceTexture = new ID3D11Texture2D(pTexture);

                var desc = sourceTexture.Description;
                _stagingTexture = _d3D11Device.CreateTexture2D(
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
                        MiscFlags = ResourceOptionFlags.None
                    });

                var immediateContext = _d3D11Device.ImmediateContext;
                immediateContext.CopyResource(_stagingTexture, sourceTexture);

                var mapBox = immediateContext.Map(_stagingTexture, 0);
                if (mapBox.DataPointer == 0)
                    throw new InvalidOperationException("Failed to map staging texture.");

                if (_disposed)
                {
                    immediateContext.Unmap(_stagingTexture, 0);
                    return;
                }

                Data = mapBox.DataPointer;
                Stride = (int)mapBox.RowPitch;
                Size = new PixelSize((int)desc.Width, (int)desc.Height);

                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        };

        _session.StartCapture();
        _dCompositionDesktopDevice.Commit();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Data != 0 && _d3D11Device is not null && _stagingTexture is not null)
        {
            _d3D11Device.ImmediateContext.Unmap(_stagingTexture, 0);
        }

        if (_hThumbnailId != 0)
        {
            DwmUnregisterThumbnail(_hThumbnailId);
        }

        _stagingTexture?.Dispose();
        _d2DDevice?.Dispose();
        _direct3DDevice?.Dispose();
        _d3D11Device?.Dispose();

        Dispatcher.UIThread.Invoke(() =>
        {
            _session?.Dispose();
            _framePool?.Dispose();
            _dCompositionVisual?.Dispose();
            _hostWindow?.Dispose();
            // _dCompositionDevice2?.Dispose();
        });
    }

    public static async Task<IVisualElement.ICapturedBitmapData> CaptureAsync(
        nint sourceHWnd,
        PixelRect relativeRect,
        CancellationToken cancellationToken = default)
    {
        var screenCapture = await Dispatcher.UIThread.InvokeOnDemandAsync(() => new Direct3D11ScreenCapture(sourceHWnd, relativeRect));

        try
        {
            await screenCapture.CaptureFrameAsync(cancellationToken);
            return screenCapture;
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
                throw new Win32Exception(Marshal.GetLastWin32Error());
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
                case (uint)WINDOW_MESSAGE.WM_ACTIVATE:
                case (uint)WINDOW_MESSAGE.WM_SETFOCUS:
                case (uint)WINDOW_MESSAGE.WM_KILLFOCUS:
                case (uint)WINDOW_MESSAGE.WM_ACTIVATEAPP:
                case (uint)WINDOW_MESSAGE.WM_NCACTIVATE:
                    return default; // Do not activate or take focus
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
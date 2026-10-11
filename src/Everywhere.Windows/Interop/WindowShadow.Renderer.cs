using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Everywhere.Patches.Contracts.Interop;
using Everywhere.Utilities;
using Serilog;
using Vortice.Direct2D1;
using WinRT;
using Color = Windows.UI.Color;

namespace Everywhere.Windows.Interop;

internal sealed partial class WindowShadow
{
    /// <summary>
    /// Owns a host-relative, eight-slice shadow tree on Avalonia's compositor. Content drawing
    /// callbacks update only its source tile and appearance; native resizing stretches the edges
    /// without waiting for a content frame. HWND operations run on the UI thread. The patch
    /// serializes tree release with rendering before the UI thread destroys the HWND.
    /// </summary>
    private sealed class Renderer : IWindowCompositionRenderer, IDisposable
    {
        private const float ShadowPadding = 24;
        private const float ShadowBlurRadius = 24;
        private const float ActiveShadowOpacity = 200f / 255;
        private const float InactiveShadowOpacity = 100f / 255;

        private readonly SpriteVisual?[] _slices = new SpriteVisual?[9];
        private readonly CompositionVisualSurface?[] _sliceSurfaces = new CompositionVisualSurface?[9];
        private readonly CompositionSurfaceBrush?[] _sliceBrushes = new CompositionSurfaceBrush?[9];

        private HWND _hWnd;
        private bool _isVisible;
        private bool _isActive;
        private bool _hasCompositionFailed;
        private double _scaling;
        private float _radius = float.NaN;
        private nint _compositorPointer;
        private DesktopWindowTarget? _target;
        private ContainerVisual? _root;
        private ShapeVisual? _maskVisual;
        private CompositionRoundedRectangleGeometry? _maskGeometry;
        private CompositionSpriteShape? _maskShape;
        private CompositionColorBrush? _maskColorBrush;
        private CompositionVisualSurface? _maskSurface;
        private CompositionSurfaceBrush? _maskBrush;
        private SpriteVisual? _shadowVisual;
        private DropShadow? _shadow;
        private CompositionPathGeometry? _clipGeometry;
        private CompositionGeometricClip? _clip;
        private CompositionGeometrySource? _clipSource;
        private ID2D1Factory? _geometryFactory;

        private Renderer(HWND hWnd)
        {
            _hWnd = hWnd;
        }

        public static unsafe Renderer? TryCreate(HWND owner)
        {
            using var hInstance = PInvoke.GetModuleHandle();
            var window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP |
                WINDOW_EX_STYLE.WS_EX_LAYERED |
                WINDOW_EX_STYLE.WS_EX_TOOLWINDOW |
                WINDOW_EX_STYLE.WS_EX_NOACTIVATE |
                WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                "STATIC",
                "Everywhere.WindowShadow",
                WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_DISABLED,
                0,
                0,
                1,
                1,
                hWndParent: owner,
                hInstance: hInstance);
            if (window.IsNull)
            {
                LogWin32Failure("create the window shadow", owner);
                return null;
            }

            var renderer = new Renderer(window);
            try
            {
                // Composition supplies per-pixel transparency. Layering with full global opacity
                // exists only to make WS_EX_TRANSPARENT pass input through to other processes.
                if (!PInvoke.SetLayeredWindowAttributes(window, default, byte.MaxValue, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
                {
                    LogWin32Failure("enable input transparency for the window shadow", window);
                    renderer.Dispose();
                    return null;
                }

                fixed (char* propertyName = "UIA_WindowVisibilityOverridden")
                {
                    if (!PInvoke.SetProp(window, new PCWSTR(propertyName), new HANDLE(2)))
                    {
                        LogWin32Failure("hide the window shadow from UI Automation", window);
                    }
                }

                return renderer;
            }
            catch (Exception exception)
            {
                Log.ForContext<Renderer>().Error(exception, "Failed to initialize shadow HWND {WindowHandle}", window);
                renderer.Dispose();
                return null;
            }
        }

        private unsafe void InitializeComposition(nint compositorPointer)
        {
            // FromAbi retains a COM reference. Do not call Compositor.Dispose: its IClosable.Close
            // would shut down Avalonia's shared compositor, not merely release this projection.
            var compositor = Compositor.FromAbi(compositorPointer);
            var targetPointer = compositor.As<ICompositorDesktopInterop>().CreateDesktopWindowTarget((nint)_hWnd.Value, true);
            try
            {
                // The interop flag selects composition's position relative to child HWNDs,
                // not the top-level window's WS_EX_TOPMOST state.
                _target = DesktopWindowTarget.FromAbi(targetPointer);
            }
            finally
            {
                Marshal.Release(targetPointer);
            }

            _maskGeometry = compositor.CreateRoundedRectangleGeometry();
            _maskColorBrush = compositor.CreateColorBrush(new Color { A = 255, R = 255, G = 255, B = 255 });
            _maskShape = compositor.CreateSpriteShape(_maskGeometry);
            _maskShape.FillBrush = _maskColorBrush;
            _maskVisual = compositor.CreateShapeVisual();
            _maskVisual.Shapes.Add(_maskShape);
            _maskSurface = compositor.CreateVisualSurface();
            _maskSurface.SourceVisual = _maskVisual;
            _maskBrush = compositor.CreateSurfaceBrush(_maskSurface);
            _maskBrush.Stretch = CompositionStretch.None;

            _shadow = compositor.CreateDropShadow();
            _shadow.Color = new Color { A = 255 };
            _shadow.Mask = _maskBrush;
            _shadowVisual = compositor.CreateSpriteVisual();
            _shadowVisual.Shadow = _shadow;

            _clipGeometry = compositor.CreatePathGeometry();
            _clip = compositor.CreateGeometricClip(_clipGeometry);
            _shadowVisual.Clip = _clip;
            _root = compositor.CreateContainerVisual();
            _root.RelativeSizeAdjustment = Vector2.One;
            _target.Root = _root;

            for (var index = 0; index < _slices.Length; index++)
            {
                if (index == 4) continue;
                var surface = _sliceSurfaces[index] = compositor.CreateVisualSurface();
                surface.SourceVisual = _shadowVisual;
                var brush = _sliceBrushes[index] = compositor.CreateSurfaceBrush(surface);
                brush.Stretch = CompositionStretch.Fill;
                var slice = _slices[index] = compositor.CreateSpriteVisual();
                slice.Brush = brush;
                var x = index % 3;
                var y = index / 3;
                slice.RelativeSizeAdjustment = new Vector2(x == 1 ? 1 : 0, y == 1 ? 1 : 0);
                slice.RelativeOffsetAdjustment = new Vector3(x == 2 ? 1 : 0, y == 2 ? 1 : 0, 0);
                _root.Children.InsertAtTop(slice);
            }

            _geometryFactory = D2D1.D2D1CreateFactory<ID2D1Factory>();
            _compositorPointer = compositorPointer;
        }

        /// <inheritdoc />
        public void Update(nint compositorPointer, double scaling, float radius)
        {
            if (_hasCompositionFailed) return;

            try
            {
                if (_compositorPointer != compositorPointer)
                {
                    ReleaseResources();
                    InitializeComposition(compositorPointer);
                }

                if (_maskGeometry is null || _maskVisual is null || _maskSurface is null ||
                    _shadowVisual is null || _shadow is null)
                {
                    return;
                }

                var padding = (int)Math.Ceiling(ShadowPadding * scaling);
                // Keep the curved corner and the blur's transition to a straight edge in a fixed
                // tile. Only the straight sections stretch; HWND resizing needs no new scene frame.
                var corner = (int)Math.Ceiling(padding + radius + ShadowBlurRadius * scaling * 1.5);
                var middle = Math.Max(1, (int)Math.Ceiling(32 * scaling));
                var size = corner * 2 + middle;

                // ReSharper disable CompareOfFloatsByEqualityOperator
                if (_scaling != scaling || _radius != radius)
                    // ReSharper restore CompareOfFloatsByEqualityOperator
                {
                    var visualSize = new Vector2(size, size);
                    _maskVisual.Size = visualSize;
                    _maskGeometry.Offset = new Vector2(padding);
                    _maskGeometry.Size = new Vector2(size - padding * 2, size - padding * 2);
                    _maskGeometry.CornerRadius = new Vector2(radius);
                    _maskSurface.SourceSize = visualSize;
                    _shadowVisual.Size = visualSize;
                    _shadow.BlurRadius = ShadowBlurRadius * (float)scaling;
                    UpdateClip(size, size, padding, radius);

                    for (var index = 0; index < _slices.Length; index++)
                    {
                        if (_slices[index] is not { } slice || _sliceSurfaces[index] is not { } surface) continue;

                        var x = index % 3;
                        var y = index / 3;
                        surface.SourceOffset = new Vector2(
                            x switch
                            {
                                0 => 0,
                                1 => corner,
                                _ => size - corner
                            },
                            y switch
                            {
                                0 => 0,
                                1 => corner,
                                _ => size - corner
                            });
                        surface.SourceSize = new Vector2(x == 1 ? middle : corner, y == 1 ? middle : corner);
                        slice.Size = new Vector2(x == 1 ? -corner * 2 : corner, y == 1 ? -corner * 2 : corner);
                        slice.Offset = new Vector3(
                            x switch
                            {
                                0 => 0,
                                1 => corner,
                                _ => -corner
                            },
                            y switch
                            {
                                0 => 0,
                                1 => corner,
                                _ => -corner
                            },
                            0);
                    }
                    _scaling = scaling;
                    _radius = radius;
                }

                _shadow.Opacity = Volatile.Read(ref _isActive) ? ActiveShadowOpacity : InactiveShadowOpacity;
            }
            catch (Exception exception)
            {
                Log.ForContext<Renderer>().Error(exception, "Failed to update Composition for shadow HWND {WindowHandle}", _hWnd);
                ReleaseResources();
                _hasCompositionFailed = true;
            }
        }

        public bool Position(RECT frame, double scaling, bool isActive)
        {
            if (_hWnd.IsNull) return false;

            Volatile.Write(ref _isActive, isActive);
            var padding = (int)Math.Ceiling(ShadowPadding * scaling);
            var width = frame.Width + padding * 2;
            var height = frame.Height + padding * 2;

            var flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
            if (!_isVisible) flags |= SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;
            if (!PInvoke.SetWindowPos(_hWnd, HWND.Null, frame.X - padding, frame.Y - padding, width, height, flags))
            {
                LogWin32Failure("position the window shadow", _hWnd);
                return false;
            }

            _isVisible = true;
            return true;
        }

        private void UpdateClip(int width, int height, int padding, float radius)
        {
            if (_geometryFactory is null || _clipGeometry is null) return;

            // Owned HWNDs sit above their owner. Subtract its rounded client shape from the final
            // shadow output so neither the solid mask nor the blur darkens the owner's acrylic.
            using var outer = _geometryFactory.CreateRectangleGeometry(
                new RectangleF(0, 0, width, height));
            using var inner = _geometryFactory.CreateRoundedRectangleGeometry(
                new RoundedRectangle(new RectangleF(padding, padding, width - padding * 2, height - padding * 2), radius, radius));
            var source = new CompositionGeometrySource(_geometryFactory.CreateGeometryGroup(FillMode.Alternate, [outer, inner]));

            try
            {
                _clipGeometry.Path = source.CreatePath();
            }
            catch
            {
                source.Dispose();
                throw;
            }

            DisposeHelper.DisposeToDefault(ref _clipSource);
            _clipSource = source;
        }

        public bool SynchronizeTopmost(HWND owner)
        {
            if (_hWnd.IsNull || !_isVisible) return true;

            // Avalonia caches Topmost independently of native operations such as HWND_BOTTOM.
            // Read both HWNDs after the owner's window-management operation has completed.
            var isOwnerTopmost = IsTopmost(owner);
            if (IsTopmost(_hWnd) == isOwnerTopmost) return true;

            const SET_WINDOW_POS_FLAGS flags =
                SET_WINDOW_POS_FLAGS.SWP_NOMOVE |
                SET_WINDOW_POS_FLAGS.SWP_NOSIZE |
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE |
                SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

            if (!PInvoke.SetWindowPos(
                    _hWnd,
                    isOwnerTopmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST,
                    0,
                    0,
                    0,
                    0,
                    flags))
            {
                LogWin32Failure("synchronize the window shadow's native Topmost state", _hWnd);
                return false;
            }

            // Changing the Topmost group initially puts the shadow at that group's front.
            // Return it immediately above its owner without raising the owner or covering windows
            // already in front of it. Never use a topmost anchor for a non-topmost owner.
            var insertAfter = PInvoke.GetWindow(owner, GET_WINDOW_CMD.GW_HWNDPREV);
            if (insertAfter != _hWnd)
            {
                if (insertAfter.IsNull || IsTopmost(insertAfter) != isOwnerTopmost)
                {
                    insertAfter = isOwnerTopmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST;
                }

                if (!PInvoke.SetWindowPos(_hWnd, insertAfter, 0, 0, 0, 0, flags))
                {
                    LogWin32Failure("restore the window shadow's owner-relative Z-order", _hWnd);
                    return false;
                }
            }

            var isActualOwnerTopmost = IsTopmost(owner);
            var isActualShadowTopmost = IsTopmost(_hWnd);
            if (isActualOwnerTopmost == isOwnerTopmost && isActualShadowTopmost == isOwnerTopmost)
            {
                return true;
            }

            Log.ForContext<Renderer>().Error(
                "Failed to synchronize native Topmost for shadow HWND {ShadowHandle} and owner HWND {OwnerHandle}: " +
                "expected {ExpectedTopmost}, owner {OwnerTopmost}, shadow {ShadowTopmost}",
                _hWnd,
                owner,
                isOwnerTopmost,
                isActualOwnerTopmost,
                isActualShadowTopmost);
            return false;
        }

        private static bool IsTopmost(HWND window)
        {
            var exStyle = PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            return (exStyle & (int)WINDOW_EX_STYLE.WS_EX_TOPMOST) != 0;
        }

        public void Hide()
        {
            if (_hWnd.IsNull) return;
            PInvoke.ShowWindow(_hWnd, SHOW_WINDOW_CMD.SW_HIDE);
            _isVisible = false;
        }

        /// <inheritdoc />
        public void ReleaseResources()
        {
            _target?.Root = null;
            _root?.Children.RemoveAll();

            for (var index = 0; index < _slices.Length; index++)
            {
                if (_sliceSurfaces[index] is { } surface) surface.SourceVisual = null;
                DisposeHelper.DisposeToDefault(ref _slices[index]);
                DisposeHelper.DisposeToDefault(ref _sliceBrushes[index]);
                DisposeHelper.DisposeToDefault(ref _sliceSurfaces[index]);
            }

            DisposeHelper.DisposeToDefault(ref _root);
            _shadowVisual?.Shadow = null;
            _shadowVisual?.Clip = null;
            _maskSurface?.SourceVisual = null;

            DisposeHelper.DisposeToDefault(ref _target);
            DisposeHelper.DisposeToDefault(ref _shadowVisual);
            DisposeHelper.DisposeToDefault(ref _shadow);
            DisposeHelper.DisposeToDefault(ref _clip);
            DisposeHelper.DisposeToDefault(ref _clipGeometry);
            DisposeHelper.DisposeToDefault(ref _clipSource);
            DisposeHelper.DisposeToDefault(ref _maskBrush);
            DisposeHelper.DisposeToDefault(ref _maskSurface);
            DisposeHelper.DisposeToDefault(ref _maskVisual);
            DisposeHelper.DisposeToDefault(ref _maskShape);
            DisposeHelper.DisposeToDefault(ref _maskColorBrush);
            DisposeHelper.DisposeToDefault(ref _maskGeometry);
            DisposeHelper.DisposeToDefault(ref _geometryFactory);
            _compositorPointer = 0;
            _scaling = 0;
            _radius = float.NaN;
            _hasCompositionFailed = false;
        }

        public void Dispose()
        {
            // The window unregisters this renderer (and releases its tree) before reaching this point.
            Hide();

            if (_hWnd.IsNull) return;
            if (PInvoke.DestroyWindow(_hWnd))
            {
                _hWnd = HWND.Null;
                return;
            }

            var errorCode = Marshal.GetLastPInvokeError();
            if (!PInvoke.IsWindow(_hWnd))
            {
                _hWnd = HWND.Null;
                return;
            }

            LogWin32Failure("destroy the window shadow", _hWnd, errorCode);
        }
    }
}
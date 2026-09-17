using System.Runtime.InteropServices;
using Avalonia.OpenGL.Egl;
using Everywhere.Patches.Contracts.Interop;
using MicroCom.Runtime;
using MonoMod;
using Vortice.DirectComposition;

// Avalonia's DirectComposition bindings are internal. These declarations provide
// only the members consumed by the patch and are relinked to Avalonia's bindings
// when MonoMod weaves the target assembly.
// ReSharper disable once CheckNamespace
namespace Avalonia.Win32.DComposition;

[MonoModIgnore]
internal interface IDCompositionDevice2 : IUnknown;

[MonoModIgnore]
internal unsafe interface IDCompositionVisual : IUnknown
{
    void SetClip_IDCompositionClip(void* clip);
}

[MonoModPatch("Avalonia.Win32.DComposition.DirectCompositedWindow")]
internal class patch_DirectCompositedWindow : IDisposable
{
    [MonoModIgnore]
    private readonly DirectCompositionShared _shared = null!;

    [MonoModIgnore]
    public extern EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo WindowInfo { get; }

    [MonoModIgnore]
    private readonly IDCompositionVisual _container = null!;

    [MonoModIgnore]
    private readonly IDCompositionDevice2 _device = null!;

    private IDCompositionRectangleClip? _everywhereCornerClip;
    private PixelSize _everywhereCornerSize;
    private double _everywhereCornerScaling;
    private float _everywhereCornerRadius = float.NaN;

    private static bool TryGetCornerRadius(
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo windowInfo,
        out double radius)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (windowInfo is IWindowCornerRadiusFeature feature)
            return feature.TryGetEffectiveCornerRadius(out radius);

        radius = 0;
        return false;
    }

    // The wrapper below must not use MonoModReplace: MonoMod needs to preserve
    // Avalonia's implementation under this orig_ name before applying the wrapper.
    public extern IDisposable orig_BeginTransaction();

    public IDisposable BeginTransaction()
    {
        var transaction = orig_BeginTransaction();
        try
        {
            UpdateEverywhereCornerClip();
            return transaction;
        }
        catch
        {
            transaction.Dispose();
            throw;
        }
    }

    private unsafe void UpdateEverywhereCornerClip()
    {
        if (!TryGetCornerRadius(WindowInfo, out var logicalRadius))
            return;

        var size = WindowInfo.Size;
        var scaling = WindowInfo.Scaling;
        var radius = (float)(logicalRadius * scaling);
        var needsCreation = _everywhereCornerClip is null;
        var sizeChanged = _everywhereCornerSize != size;
        var scaleChanged = Math.Abs(_everywhereCornerScaling - scaling) > double.Epsilon;
        var radiusChanged = float.IsNaN(_everywhereCornerRadius) || Math.Abs(_everywhereCornerRadius - radius) > float.Epsilon;

        if (needsCreation)
        {
            // Keep the clip in the DirectComposition tree; SetWindowRgn would lose antialiasing.
            var devicePointer = _device.GetNativeIntPtr();
            Marshal.AddRef(devicePointer);
            using var device = new Vortice.DirectComposition.IDCompositionDevice2(devicePointer);
            _everywhereCornerClip = device.CreateRectangleClip();
        }

        var clip = _everywhereCornerClip;
        if (clip is null)
            return;

        if (needsCreation || sizeChanged || scaleChanged)
        {
            clip.SetLeft(0);
            clip.SetTop(0);
            clip.SetRight(size.Width);
            clip.SetBottom(size.Height);
        }

        if (needsCreation || radiusChanged)
        {
            clip.SetTopLeftRadiusX(radius);
            clip.SetTopLeftRadiusY(radius);
            clip.SetTopRightRadiusX(radius);
            clip.SetTopRightRadiusY(radius);
            clip.SetBottomLeftRadiusX(radius);
            clip.SetBottomLeftRadiusY(radius);
            clip.SetBottomRightRadiusX(radius);
            clip.SetBottomRightRadiusY(radius);
        }

        if (needsCreation)
            _container.SetClip_IDCompositionClip((void*)clip.NativePointer);

        _everywhereCornerSize = size;
        _everywhereCornerScaling = scaling;
        _everywhereCornerRadius = radius;
    }

    // Keep Avalonia's original disposal path so its DirectComposition objects are released.
    public extern void orig_Dispose();

    public unsafe void Dispose()
    {
        lock (_shared.SyncRoot)
        {
            if (_everywhereCornerClip is not null)
            {
                _container.SetClip_IDCompositionClip(null);
                _everywhereCornerClip.Dispose();
                _everywhereCornerClip = null;
            }

            orig_Dispose();
        }
    }
}

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Surfaces;
using Avalonia.Win32.WinRT.Composition;
using Everywhere.Patches.Contracts.Interop;
using MonoMod;
using static Avalonia.Win32.Interop.UnmanagedMethods;

namespace Everywhere.Patches.Avalonia.Win32;

[MonoModPatch("Avalonia.Win32.WindowImpl")]
internal partial class patch_WindowImpl : IWindowCompositionFeature
{
    [MonoModIgnore]
    private readonly IInputRoot? _owner = null;

    [MonoModIgnore]
    private readonly IPlatformRenderSurface? _glSurface = null;

    // ReSharper disable once SuspiciousTypeConversion.Global
    public bool IsCustomCornerRadiusSupported => _glSurface is WinUiCompositedWindowSurface;

    private long _everywhereCornerRadiusBits;
    private int _everywhereCornerRadiusConfigured;
    private int _everywhereCornerRadiusSuppressed;
    private IWindowCompositionRenderer? _everywhereRenderer;

    public void SetCornerRadius(double radius)
    {
        if (!double.IsFinite(radius) || radius < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radius), radius, "The corner radius must be finite and non-negative.");
        }

        radius = Math.Min(radius, 4096);
        Volatile.Write(ref _everywhereCornerRadiusBits, BitConverter.DoubleToInt64Bits(radius));
        Volatile.Write(ref _everywhereCornerRadiusConfigured, 1);
    }

    public void SetCornerRadiusSuppressed(bool suppressed) =>
        Volatile.Write(ref _everywhereCornerRadiusSuppressed, suppressed ? 1 : 0);

    public bool TryGetEffectiveCornerRadius(out double radius)
    {
        if (Volatile.Read(ref _everywhereCornerRadiusConfigured) == 0)
        {
            radius = 0;
            return false;
        }

        if (Volatile.Read(ref _everywhereCornerRadiusSuppressed) != 0)
        {
            radius = 0;
            return true;
        }

        var bits = Volatile.Read(ref _everywhereCornerRadiusBits);
        radius = BitConverter.Int64BitsToDouble(bits);
        return true;
    }

    public void SetCustomRenderer(IWindowCompositionRenderer? renderer)
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (_glSurface is not WinUiCompositedWindowSurface surface)
        {
            if (renderer is not null) throw new NotSupportedException("Custom rendering requires WinUI Composition.");
            return;
        }

        // Use the same transaction lock as drawing, commits and surface disposal. After this
        // returns, the UI thread can destroy the old renderer's HWND without an in-flight update.
        lock (surface.SyncRoot)
        {
            if (ReferenceEquals(_everywhereRenderer, renderer)) return;
            var previous = _everywhereRenderer;
            _everywhereRenderer = null;
            previous?.ReleaseResources();
            _everywhereRenderer = renderer;
        }
    }

    public void RequestCompositionFrame()
    {
        if (_owner?.FocusRoot is TopLevel topLevel)
        {
            // Invalidating an unchanged draw list can produce no pixel damage. RequestRedraw
            // ensures BeginDraw runs, while InvalidateVisual schedules the usual async commit.
            topLevel.Renderer.CompositionTarget.RequestRedraw();
            topLevel.InvalidateVisual();
        }
    }

    public void SynchronizeCornerClip()
    {
        // ReSharper disable once SuspiciousTypeConversion.Global
        if (_glSurface is WinUiCompositedWindowSurface surface)
            surface.SynchronizeCornerClip();
    }

    internal void UpdateEverywhereComposition(nint compositor, double scaling, float radius)
    {
        // Called only inside WinUiCompositedWindow's existing composition transaction.
        _everywhereRenderer?.Update(compositor, scaling, radius);
    }

    internal void ReleaseEverywhereComposition()
    {
        // The caller holds the same shared lock. Retain registration for surface recreation.
        _everywhereRenderer?.ReleaseResources();
    }

    private extern RECT orig_ClientRectToWindowRect(
        RECT clientRect,
        WindowStyles? styleOverride = null,
        WindowStyles? extendedStyleOverride = null);

    private RECT ClientRectToWindowRect(
        RECT clientRect,
        WindowStyles? styleOverride = null,
        WindowStyles? extendedStyleOverride = null)
    {
        // The custom frame handles WM_NCCALCSIZE by making the complete restored
        // window rectangle client area. Do not let Avalonia add the native caption
        // and resize-frame metrics to a requested client size a second time.
        //
        // Keep explicit style conversions intact. Resize() uses one to calculate its
        // extended-client-area top correction, including the collapsed caption pixel.
        if (Volatile.Read(ref _everywhereCornerRadiusConfigured) != 0 &&
            styleOverride is null &&
            extendedStyleOverride is null)
        {
            return clientRect;
        }

        return orig_ClientRectToWindowRect(clientRect, styleOverride, extendedStyleOverride);
    }
}
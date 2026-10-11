using System.Numerics;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;
using Everywhere.Patches.Avalonia.Win32;
using Everywhere.Patches.Contracts.Interop;
using MicroCom.Runtime;
using MonoMod;

// Avalonia's WinRT bindings are internal by design. These declarations provide the
// compile-time shape required by the patch; MonoMod relinks them to the matching
// internal types in Avalonia.Win32 when weaving the assembly.
namespace Avalonia.Win32.WinRT
{
    [MonoModIgnore]
    internal interface ICompositor : IUnknown;

    [MonoModIgnore]
    internal interface ICompositor5
    {
        IAsyncAction RequestCommitAsync();
    }

    [MonoModIgnore]
    internal interface IAsyncAction : IDisposable;

    [MonoModIgnore]
    internal interface IVisual;

    [MonoModIgnore]
    internal interface ICompositionRoundedRectangleGeometry : IDisposable
    {
        void SetCornerRadius(Vector2 value);

        void SetOffset(Vector2 value);

        void SetSize(Vector2 value);
    }
}

namespace Avalonia.Win32.WinRT.Composition
{
    [MonoModPatch("Avalonia.Win32.WinRT.Composition.WinUiCompositedWindowSurface")]
    internal class WinUiCompositedWindowSurface
    {
        [MonoModIgnore]
        private readonly WinUiCompositionShared _shared = null!;

        [MonoModIgnore]
        private patch_WinUiCompositedWindow? _window = null;

        // Available before the first render target is created and stable across its recreation.
        internal object SyncRoot => _shared.SyncRoot;

        internal void SynchronizeCornerClip()
        {
            lock (_shared.SyncRoot)
            {
                // Creation and recreation still belong to Avalonia's render-target path.
                if (_window?.SynchronizeEverywhereCornerClip() != true) return;

                // Submit geometry without waiting for an Avalonia content frame. Releasing
                // our action reference does not cancel the compositor's asynchronous commit.
                using var commit = _shared.Compositor5.RequestCommitAsync();
            }
        }
    }

    [MonoModIgnore]
    // ReSharper disable once ClassNeverInstantiated.Global
    internal class WinUiCompositionShared
    {
        // This initializer only satisfies nullable analysis while compiling the patch donor.
        // MonoMod relinks the property access to Avalonia.Win32's live compositor instance.
        public ICompositor Compositor { get; } = null!;

        public ICompositor5 Compositor5 { get; } = null!;

        // This is likewise a compile-time shape declaration. After weaving, it resolves to
        // Avalonia.Win32's shared synchronization object rather than this null initializer.
        public object SyncRoot { get; } = null!;
    }

    [MonoModPatch("Avalonia.Win32.WinRT.Composition.WinUiCompositedWindow")]
    internal class patch_WinUiCompositedWindow : IDisposable
    {
        private const float BackdropClipInset = 2;

        [MonoModIgnore]
        private readonly WinUiCompositionShared _shared = null!;

        [MonoModIgnore]
        private readonly IVisual? _micaLight = null;

        [MonoModIgnore]
        private readonly IVisual? _micaDark = null;

        [MonoModIgnore]
        private readonly IVisual _blur = null!;

        [MonoModIgnore]
        private readonly IVisual _visual = null!;

        [MonoModIgnore]
        public extern EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo WindowInfo { get; }

        private ICompositionRoundedRectangleGeometry? _everywhereCornerGeometry;
        private ICompositionRoundedRectangleGeometry? _everywhereBackdropCornerGeometry;
        private PixelSize _everywhereCornerSize;
        private float _everywhereCornerRadius = float.NaN;
        private bool _isEverywhereDisposed;

        [MonoModLinkTo("Avalonia.Win32.WinRT.Composition.WinUiCompositionUtils", "ClipVisual")]
        private static extern ICompositionRoundedRectangleGeometry? ClipVisual(
            ICompositor compositor,
            float? cornerRadius,
            params IVisual?[] visuals);

        private static bool TryGetCornerRadius(
            EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo windowInfo,
            out double radius)
        {
            // ReSharper disable once SuspiciousTypeConversion.Global
            // Avalonia's Win32 platform implementation is extended at runtime to implement IWindowCompositionFeature.
            if (windowInfo is IWindowCompositionFeature feature)
                return feature.TryGetEffectiveCornerRadius(out radius);

            radius = 0;
            return false;
        }

        internal void UpdateEverywhereComposition(PixelSize size, double scaling)
        {
            // BeginDraw holds the shared lock until the drawing session ends. Content can lag
            // behind HWND resize, so its scene size must never drive the outer window clip.
            if (!TryGetCornerRadius(WindowInfo, out var logicalRadius))
                return;

            var radius = Math.Clamp((float)(logicalRadius * scaling), 0, Math.Min(size.Width, size.Height) / 2f);
            SynchronizeEverywhereCornerClip();

            // ReSharper disable once SuspiciousTypeConversion.Global
            if (WindowInfo is patch_WindowImpl window)
                window.UpdateEverywhereComposition(_shared.Compositor.GetNativeIntPtr(), scaling, radius);
        }

        internal bool SynchronizeEverywhereCornerClip()
        {
            // Both callers hold SyncRoot. WindowInfo.Size reads GetClientRect directly;
            // no cached layout size or asynchronous render-scene size participates here.
            if (_isEverywhereDisposed || !TryGetCornerRadius(WindowInfo, out var logicalRadius)) return false;
            var size = WindowInfo.Size;
            var radius = Math.Clamp((float)(logicalRadius * WindowInfo.Scaling), 0, Math.Min(size.Width, size.Height) / 2f);
            return UpdateEverywhereCornerClip(size, radius);
        }

        private bool UpdateEverywhereCornerClip(PixelSize size, float radius)
        {
            var needsCreation = _everywhereCornerGeometry is null;
            var sizeChanged = _everywhereCornerSize != size;
            var radiusChanged = float.IsNaN(_everywhereCornerRadius) || Math.Abs(_everywhereCornerRadius - radius) > float.Epsilon;

            if (!needsCreation && !sizeChanged && !radiusChanged) return false;

            if (needsCreation)
            {
                // Keep the rendered content on the requested outer boundary. The backdrop uses
                // a separate, slightly inset clip so its bright edge sampling cannot appear as
                // a second border outside the Avalonia-rendered border.
                _everywhereCornerGeometry = ClipVisual(
                    _shared.Compositor,
                    radius,
                    _visual);
                _everywhereBackdropCornerGeometry = ClipVisual(
                    _shared.Compositor,
                    Math.Max(0, radius - BackdropClipInset),
                    _blur,
                    _micaLight,
                    _micaDark);
            }
            var geometry = _everywhereCornerGeometry;
            if (geometry is null)
                return false;

            if (!needsCreation && radiusChanged)
                geometry.SetCornerRadius(new Vector2(radius, radius));

            if (needsCreation || sizeChanged)
                geometry.SetSize(new Vector2(size.Width, size.Height));

            var backdropGeometry = _everywhereBackdropCornerGeometry;
            if (backdropGeometry is not null)
            {
                // Maximized and snapped windows suppress their radius. Do not leave a one-pixel
                // backdrop gap along their square edges in that state.
                var backdropInset = radius > 0 ? BackdropClipInset : 0;
                if (needsCreation || radiusChanged)
                {
                    backdropGeometry.SetCornerRadius(new Vector2(
                        Math.Max(0, radius - backdropInset),
                        Math.Max(0, radius - backdropInset)));
                    backdropGeometry.SetOffset(new Vector2(backdropInset, backdropInset));
                }

                if (needsCreation || sizeChanged || radiusChanged)
                {
                    backdropGeometry.SetSize(new Vector2(
                        Math.Max(0, size.Width - backdropInset * 2),
                        Math.Max(0, size.Height - backdropInset * 2)));
                }
            }

            _everywhereCornerSize = size;
            _everywhereCornerRadius = radius;
            return true;
        }

        // Keep Avalonia's original disposal path so all of its COM objects are released.
        public extern void orig_Dispose();

        public void Dispose()
        {
            lock (_shared.SyncRoot)
            {
                _isEverywhereDisposed = true;
                // ReSharper disable once SuspiciousTypeConversion.Global
                if (WindowInfo is patch_WindowImpl window)
                    window.ReleaseEverywhereComposition();

                _everywhereCornerGeometry?.Dispose();
                _everywhereCornerGeometry = null;
                _everywhereBackdropCornerGeometry?.Dispose();
                _everywhereBackdropCornerGeometry = null;
                orig_Dispose();
            }
        }
    }

    [MonoModPatch("Avalonia.Win32.WinRT.Composition.WinUiCompositedWindowRenderTarget")]
    internal class patch_WinUiCompositedWindowRenderTarget
    {
        [MonoModIgnore]
        private readonly patch_WinUiCompositedWindow _window = null!;

        // Preserve Avalonia's surface creation, device-loss handling and drawing-session lifetime.
        public extern IDirect3D11TextureRenderTargetRenderSession orig_BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo);

        public IDirect3D11TextureRenderTargetRenderSession BeginDraw(IRenderTarget.RenderTargetSceneInfo sceneInfo)
        {
            var session = orig_BeginDraw(sceneInfo);
            try
            {
                _window.UpdateEverywhereComposition(sceneInfo.Size, sceneInfo.Scaling);
                return session;
            }
            catch
            {
                session.Dispose();
                throw;
            }
        }
    }
}

using Avalonia;

namespace Everywhere.Interop;

/// <summary>
/// Placeholder <see cref="IOverlayInputWatcher"/> for platforms where global input observation is
/// not implemented yet.
/// </summary>
/// <remarks>
/// Reports <see cref="IsSupported"/> as false and never dismisses. Overlays can remain usable
/// through explicit close controls without global dismissal or pointer-distance effects.
/// </remarks>
public sealed class UnsupportedOverlayInputWatcher : IOverlayInputWatcher
{
    public bool IsSupported => false;

    public PixelPoint? PointerPosition => null;

    public IOverlayInputWatch Watch(PixelRect bounds, Action onDismiss, Action<PixelPoint> onPointerMoved) => NoWatch.Instance;

    private sealed class NoWatch : IOverlayInputWatch
    {
        internal static readonly NoWatch Instance = new();

        public void Update(PixelRect bounds, IReadOnlyList<PixelRect>? popupBounds = null, IWindowInputCapture? inputCapture = null)
        {
        }

        public void Dispose() { }
    }
}

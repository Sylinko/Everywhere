using Avalonia;

namespace Everywhere.Interop;

/// <summary>
/// Placeholder <see cref="IOverlayDismissWatcher"/> for platforms where global input observation is
/// not implemented yet.
/// </summary>
/// <remarks>
/// Reports <see cref="IsSupported"/> as false and never dismisses, so features that depend on it can
/// disable themselves rather than showing an overlay the user would be unable to dismiss. This is
/// preferred over throwing, which would surface as a crash at an unrelated call site.
/// </remarks>
public sealed class UnsupportedOverlayDismissWatcher : IOverlayDismissWatcher
{
    public bool IsSupported => false;

    public IOverlayDismissWatch Watch(PixelRect bounds, Action onDismiss) => NoWatch.Instance;

    private sealed class NoWatch : IOverlayDismissWatch
    {
        internal static readonly NoWatch Instance = new();

        public void Update(PixelRect bounds) { }

        public void Dispose() { }
    }
}

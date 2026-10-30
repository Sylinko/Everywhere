using System.Reflection;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Reusable Wayland input-region bridge for Avalonia 12.1.3 toplevels. Only the
/// UI-side generated proxy is inspected; protocol objects remain on their worker.
/// This narrow private-API dependency must be revalidated on an Avalonia upgrade.
/// </summary>
public static class WaylandSurfaceInput
{
    /// <summary>
    /// Queues a full or empty input region on the surface's owning Wayland worker.
    /// Returns false while no surface proxy exists; the owner retries after mapping.
    /// The backend caches and reapplies the policy on reconnect.
    /// </summary>
    public static bool TrySetHitTestVisible(IWindowImpl nativeWindow, bool isHitTestVisible)
    {
        Dispatcher.UIThread.VerifyAccess();
        var proxy = nativeWindow.GetType()
            .GetProperty("SurfaceProxy", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?
            .GetValue(nativeWindow);
        if (proxy is null)
        {
            return false;
        }

        var method = proxy.GetType().GetMethod("SetHitTestVisible", [typeof(bool)]);
        if (method is null)
        {
            throw new NotSupportedException("This Wayland backend cannot make a passive Everywhere surface input-transparent.");
        }
        method.Invoke(proxy, [isHitTestVisible]);
        return true;
    }
}
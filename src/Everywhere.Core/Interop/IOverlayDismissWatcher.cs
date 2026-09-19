using Avalonia;

namespace Everywhere.Interop;

/// <summary>
/// Observes global input to decide when a non-activating overlay should be dismissed.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a non-activating overlay cannot rely on losing focus. Overlays that call
/// <see cref="IWindowHelper.SetFocusable"/> with <c>false</c> never receive activation in the first
/// place, so <c>Window.Deactivated</c> never fires and the usual "hide on blur" approach is
/// unavailable. Dismissal has to be inferred from global input instead.
/// </para>
/// <para>
/// Implementers must install OS hooks only while at least one watch is active, mirroring the
/// subscriber-refcounting contract of <see cref="IVisualElementContext"/>, so that no input hook
/// exists while no overlay is visible.
/// </para>
/// <para>
/// Callbacks may arrive on a hook thread. Callers are responsible for marshalling to the UI thread.
/// Implementers must never suppress the observed input: a click that dismisses an overlay must still
/// reach the window underneath it.
/// </para>
/// </remarks>
public interface IOverlayDismissWatcher
{
    /// <summary>
    /// Whether this platform can observe global input. When false, <see cref="Watch"/> returns a
    /// no-op handle and callers should not show an overlay that depends on being dismissed.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Begins watching for input that should dismiss an overlay occupying <paramref name="bounds"/>.
    /// </summary>
    /// <param name="bounds">
    /// The overlay's bounds in physical screen pixels. Passed by value rather than read back from the
    /// window because the hit test runs on a hook thread, where touching window properties would
    /// cross the UI thread boundary on every mouse event. Re-arm with a new watch when the overlay moves.
    /// </param>
    /// <param name="onDismiss">
    /// Invoked at most once per arming, on an unspecified thread, when the overlay should be dismissed.
    /// Never invoked after the returned handle is disposed.
    /// <para>
    /// Must be cheap and non-blocking — normally just posting to the UI thread. Implementations are
    /// allowed to invoke it while holding an internal lock in order to honour the "never after disposal"
    /// guarantee, so blocking here can stall the thread that observes input.
    /// </para>
    /// </param>
    /// <returns>A handle that stops watching when disposed. Disposal is idempotent.</returns>
    IOverlayDismissWatch Watch(PixelRect bounds, Action onDismiss);
}

/// <summary>
/// An active watch returned by <see cref="IOverlayDismissWatcher.Watch"/>.
/// </summary>
/// <remarks>
/// Exists so that an overlay which moves — for example a toolbar following a new text selection — can
/// update its hit-test rectangle without disposing the watch. Disposing and re-acquiring would uninstall
/// and reinstall the underlying OS hooks, which on Windows means tearing down and recreating a dedicated
/// hook thread on every move and measurably delays all input while it happens.
/// </remarks>
public interface IOverlayDismissWatch : IDisposable
{
    /// <summary>
    /// Updates the watched rectangle, in physical screen pixels, and re-arms the watch if it has already
    /// fired. Cheap enough to call on every overlay move.
    /// </summary>
    void Update(PixelRect bounds);
}

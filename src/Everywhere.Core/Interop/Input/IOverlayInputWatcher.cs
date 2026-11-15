namespace Everywhere.Interop;

/// <summary>
/// Observes global input and pointer positions for non-activating overlays.
/// </summary>
/// <remarks>
/// <para>
/// This exists because a non-activating overlay cannot rely on losing focus. Overlays that call
/// <see cref="IWindowHelper.SetWindowProperties"/> with a non-activating input mode never receive activation in the first
/// place, so <c>Window.Deactivated</c> never fires and the usual "hide on blur" approach is
/// unavailable. Dismissal has to be inferred from global input instead.
/// </para>
/// <para>
/// Implementers must install OS hooks only while at least one watch is active, so that no
/// dismissal hook exists while no overlay is visible.
/// </para>
/// <para>
/// Callbacks may arrive on a hook thread. Callers are responsible for marshalling to the UI thread.
/// Implementers must never suppress the observed input: a click that dismisses an overlay must still
/// reach the window underneath it.
/// </para>
/// </remarks>
public interface IOverlayInputWatcher
{
    /// <summary>
    /// Whether this platform can observe global input. When false, <see cref="Watch"/> returns a
    /// no-op handle. Callers must provide another way to close an overlay when observation is unavailable.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Gets the latest pointer position while a watch is active, or null when unavailable.
    /// Coordinates use the same screen space as Avalonia's PointToScreen and window Position:
    /// physical pixels on Windows and desktop points on macOS.
    /// </summary>
    PixelPoint? PointerPosition { get; }

    /// <summary>
    /// Begins watching for input that should dismiss an overlay occupying <paramref name="bounds"/>.
    /// </summary>
    /// <param name="bounds">
    /// The overlay's bounds in Avalonia screen coordinates. Passed by value rather than read back from the
    /// window because the hit test runs on a hook thread, where touching window properties would
    /// cross the UI thread boundary on every mouse event. Re-arm with a new watch when the overlay moves.
    /// </param>
    /// <param name="onPointerMoved">
    /// Invoked when the global pointer position changes, on an unspecified thread, including an initial
    /// position when available. The same cheap, non-blocking and disposal guarantees as onDismiss apply.
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
    IOverlayInputWatch Watch(PixelRect bounds, Action onDismiss, Action<PixelPoint> onPointerMoved);
}

/// <summary>
/// An active watch returned by <see cref="IOverlayInputWatcher.Watch"/>.
/// </summary>
/// <remarks>
/// Exists so that an overlay which moves — for example a toolbar following a new text selection — can
/// update its hit-test rectangle without disposing the watch. Disposing and re-acquiring would uninstall
/// and reinstall the underlying OS hooks, which on Windows means tearing down and recreating a dedicated
/// hook thread on every move and measurably delays all input while it happens.
/// </remarks>
public interface IOverlayInputWatch : IDisposable
{
    /// <summary>
    /// Updates the overlay and interactive popup rectangles in Avalonia screen coordinates and re-arms
    /// the watch. Popup bounds are copied. A supplied capture selects editing dismissal rules: keyboard
    /// input and scrolling inside the overlay do not dismiss it. The watch never owns or disposes the capture.
    /// </summary>
    /// <remarks>
    /// The caller must clear the supplied capture when it ends. Hook callbacks do not read its
    /// UI-thread state. Include interactive popups only, not passive preedit or tooltip windows.
    /// </remarks>
    void Update(PixelRect bounds, IReadOnlyList<PixelRect>? popupBounds = null, IWindowInputCapture? inputCapture = null);
}

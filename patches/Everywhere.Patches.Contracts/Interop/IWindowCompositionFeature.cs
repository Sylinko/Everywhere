namespace Everywhere.Patches.Contracts.Interop;

/// <summary>
/// Runtime contract injected into Avalonia's Windows platform implementation by MonoMod.
/// </summary>
public interface IWindowCompositionFeature
{
    /// <summary>
    /// Whether this window uses the WinUI Composition surface required by the custom frame.
    /// </summary>
    bool IsCustomCornerRadiusSupported { get; }

    /// <summary>
    /// Sets the requested corner radius in Avalonia logical pixels.
    /// </summary>
    /// <param name="radius">The requested radius. Values are clamped by the platform implementation.</param>
    void SetCornerRadius(double radius);

    /// <summary>
    /// Temporarily replaces the requested radius with zero for native window states that require square corners.
    /// </summary>
    /// <param name="suppressed">Whether the effective radius should be zero.</param>
    void SetCornerRadiusSuppressed(bool suppressed);

    /// <summary>
    /// Gets the effective radius for the current native window state.
    /// </summary>
    /// <param name="radius">The effective radius in Avalonia logical pixels.</param>
    /// <returns><see langword="true" /> when a radius has been configured for the window.</returns>
    bool TryGetEffectiveCornerRadius(out double radius);

    /// <summary>
    /// Sets the single custom renderer, or clears it with null. Call on the UI thread.
    /// Replacing or clearing waits for the current composition transaction and releases the old
    /// renderer's resources before returning. Setting the same instance again has no effect.
    /// </summary>
    /// <param name="renderer">The renderer. A non-null value requires a supported Composition surface.</param>
    void SetCustomRenderer(IWindowCompositionRenderer? renderer);

    /// <summary>
    /// Requests an asynchronous content frame for changes that do not otherwise invalidate pixels,
    /// such as shadow opacity. Must be called on the window's UI thread.
    /// </summary>
    void RequestCompositionFrame();

    /// <summary>
    /// Updates the content and backdrop clips from the current native client size and DPI,
    /// independently of content rendering. Call on the window's UI thread after native size
    /// or effective radius changes. Has no effect before the Composition window is created.
    /// </summary>
    void SynchronizeCornerClip();
}
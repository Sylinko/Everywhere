namespace Everywhere.Patches.Contracts.Interop;

/// <summary>
/// An additional visual tree using the window's borrowed native compositor. Calls are serialized
/// by Avalonia's composition transaction lock. Implementations must not synchronously dispatch
/// to the UI thread, change HWNDs, or close the borrowed compositor from either method.
/// </summary>
public interface IWindowCompositionRenderer
{
    /// <summary>
    /// Updates resources and appearance inside the content drawing transaction on the render thread.
    /// After resource release, a subsequent update must be able to recreate the visual tree.
    /// </summary>
    /// <param name="compositor">Borrowed native WinUI compositor pointer, valid during this call.</param>
    /// <param name="scaling">The content frame's physical pixels per logical pixel.</param>
    /// <param name="cornerRadius">The effective corner radius in physical pixels.</param>
    void Update(nint compositor, double scaling, float cornerRadius);

    /// <summary>
    /// Releases owned Composition resources, not the renderer or its HWND. May run on the UI or
    /// render thread and must tolerate repeated calls. Surface disposal retains registration so
    /// rendering can resume; unregistering prevents further updates before returning to the caller.
    /// </summary>
    void ReleaseResources();
}
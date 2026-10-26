namespace Everywhere.Interop;

/// <summary>
/// Synchronizes an anchored palette's first visible frame with native placement.
/// Platforms expose this feature when mapping and positioning are asynchronous.
/// Calls and continuations belong to the UI thread.
/// </summary>
public interface IWindowPresentation
{
    /// <summary>
    /// Waits for the mapped surface to reach the requested desktop bounds.
    /// Hiding or replacing that appearance cancels the pending presentation.
    /// </summary>
    Task WaitForPlacementAsync(PixelRect bounds);
}
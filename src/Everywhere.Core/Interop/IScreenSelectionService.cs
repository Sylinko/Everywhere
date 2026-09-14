using Avalonia.Media.Imaging;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Interop;

/// <summary>
/// Provides application-level interactive element and screenshot selection.
/// </summary>
public interface IScreenSelectionService
{
    /// <summary>
    /// Lets the user select a Host-owned visual element from the screen.
    /// </summary>
    /// <param name="visualContext">Main-side Host Context that adopts the confirmed remote anchor.</param>
    /// <param name="initialMode">The initial selection mode, or <see langword="null" /> to restore the previous mode.</param>
    /// <param name="cancellationToken">Cancels the active interaction.</param>
    /// <returns>The exact Host-retained candidate, or <see langword="null" /> when selection is canceled.</returns>
    Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lets the user capture an interactively selected screen region.
    /// </summary>
    /// <param name="initialMode">The initial selection mode, or <see langword="null" /> to restore the previous mode.</param>
    /// <returns>The selected screenshot, or <see langword="null" /> when selection is canceled.</returns>
    Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode);
}
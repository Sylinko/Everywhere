using Avalonia.Media.Imaging;
using Everywhere.Automation;

namespace Everywhere.Interop;

/// <summary>
/// Provides application-level interactive element and screenshot selection.
/// </summary>
public interface IScreenSelectionService
{
    /// <summary>
    /// Lets the user select a visual element from the screen.
    /// </summary>
    /// <param name="retention">The owner that retains the selected element.</param>
    /// <param name="initialMode">The initial selection mode, or <see langword="null" /> to restore the previous mode.</param>
    /// <returns>The selected and retained element, or <see langword="null" /> when selection is canceled.</returns>
    Task<VisualElementQueryResult?> PickVisualElementAsync(VisualElementRetention retention, ScreenSelectionMode? initialMode);

    /// <summary>
    /// Lets the user capture an interactively selected screen region.
    /// </summary>
    /// <param name="initialMode">The initial selection mode, or <see langword="null" /> to restore the previous mode.</param>
    /// <returns>The selected screenshot, or <see langword="null" /> when selection is canceled.</returns>
    Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode);
}
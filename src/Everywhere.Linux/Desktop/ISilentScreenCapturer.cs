using Everywhere.Automation;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Captures a named output without presenting a picker. The caller owns the returned
/// pixel buffer. This uses the existing capture contract rather than an Avalonia
/// Bitmap: Automation Host has no initialized UI renderer. Main may convert the
/// buffer to a Bitmap when displaying a frozen frame.
/// </summary>
public interface ISilentScreenCapturer
{
    /// <summary>
    /// Captures the selected output in global logical Bounds at native density,
    /// capped by IVisualElementCapture.MaximumDimension. Never substitutes another output.
    /// Availability is a backend capability, not an assumption about desktop portals.
    /// </summary>
    ValueTask<IVisualElementCapture> CaptureOutputAsync(string outputName, CancellationToken cancellationToken = default);
}
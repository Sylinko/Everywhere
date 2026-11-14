using Everywhere.Automation;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Wayland;

/// <summary>
/// Does not turn a permission/picker portal into a claimed silent-capture capability.
/// </summary>
public sealed class UnavailableWaylandScreenCapturer : ISilentScreenCapturer
{
    /// <inheritdoc />
    public ValueTask<IVisualElementCapture> CaptureOutputAsync(string outputName, CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<IVisualElementCapture>(
            new NotSupportedException("Silent capture has not been ported to this Wayland desktop."));
    }
}
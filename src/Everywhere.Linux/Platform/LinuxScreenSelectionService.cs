using Avalonia;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Automation;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Starts one interactive selection at a time. Element and screenshot modes have
/// independent histories; application-rule selection never changes either one.
/// </summary>
public sealed partial class LinuxScreenSelectionService(
    ICompositorStateProvider compositor,
    IOverlayWindowController overlays,
    ISilentScreenCapturer screenCapturer,
    INativeHelper nativeHelper) : IScreenSelectionService
{
    private static ScreenSelectionMode _previousPickMode = ScreenSelectionMode.Element;
    private static ScreenSelectionMode _previousScreenshotMode = ScreenSelectionMode.Free;
    private bool _isPicking;

    /// <inheritdoc />
    public async Task<RemoteVisualAnchor?> PickApplicationWindowAsync(
        IHostedVisualContext visualContext,
        CancellationToken cancellationToken = default)
    {
        var result = await SelectAsync(visualContext, ScreenSelectionMode.Window, SelectionPurpose.Application, cancellationToken);
        return result?.Anchor;
    }

    /// <inheritdoc />
    public async Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        var result = await SelectAsync(visualContext, initialMode ?? _previousPickMode, SelectionPurpose.Element, cancellationToken);
        return result?.Anchor;
    }

    /// <inheritdoc />
    public async Task<Bitmap?> TakeScreenshotAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        var result = await SelectAsync(visualContext, initialMode ?? _previousScreenshotMode, SelectionPurpose.Screenshot, cancellationToken);
        return result?.Screenshot;
    }

    /// <summary>
    /// Normalizes a drag without dropping its global output origin.
    /// </summary>
    public static PixelRect SelectionRectangle(PixelPoint start, PixelPoint end)
    {
        return new(
            Math.Min(start.X, end.X),
            Math.Min(start.Y, end.Y),
            Math.Abs(end.X - start.X),
            Math.Abs(end.Y - start.Y));
    }

    private async Task<SelectionResult?> SelectAsync(
        IHostedVisualContext context,
        ScreenSelectionMode mode,
        SelectionPurpose purpose,
        CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isPicking)
        {
            throw new InvalidOperationException("Another screen selection is already active.");
        }

        _isPicking = true;
        try
        {
            var session = new SelectionSession(compositor, overlays, screenCapturer, nativeHelper, context, purpose, mode, cancellationToken);
            var result = await session.RunAsync();
            if (purpose == SelectionPurpose.Screenshot)
            {
                _previousScreenshotMode = session.Mode;
            }
            else if (purpose == SelectionPurpose.Element)
            {
                _previousPickMode = session.Mode;
            }

            return result;
        }
        finally
        {
            _isPicking = false;
        }
    }

    private enum SelectionPurpose
    {
        Element,
        Application,
        Screenshot
    }

    private sealed record SelectionResult(RemoteVisualAnchor? Anchor, Bitmap? Screenshot);
}
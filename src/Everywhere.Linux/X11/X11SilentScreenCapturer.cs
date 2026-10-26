using System.Runtime.InteropServices;
using Everywhere.Automation;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Desktop;
using SkiaSharp;

namespace Everywhere.Linux.X11;

/// <summary>
/// Captures visible root pixels through an owned XCB reply, without a UI renderer.
/// </summary>
public sealed class X11SilentScreenCapturer : ISilentScreenCapturer
{
    /// <inheritdoc />
    public ValueTask<IVisualElementCapture> CaptureOutputAsync(string outputName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = new X11Connection();
        if (outputName != connection.OutputName)
        {
            throw new NotSupportedException("The requested X11 screen is unavailable.");
        }
        var pixels = connection.CaptureRoot();
        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(
            connection.Bounds.Width,
            connection.Bounds.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Opaque));
        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return ValueTask.FromResult(LinuxScreenCapture.Decode(png.ToArray(), connection.Bounds));
    }
}
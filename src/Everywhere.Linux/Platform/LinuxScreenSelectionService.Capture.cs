using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Everywhere.Automation;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Views;
using SkiaSharp;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Linux.Platform;


partial class LinuxScreenSelectionService
{
    /// <summary>
    /// Maps a logical region to native pixels without stretching rounded output bounds.
    /// </summary>
    public static PixelRect CapturePixelBounds(PixelRect region, PixelRect output, double density, PixelSize pixels)
    {
        return new PixelRect(
                (int)Math.Floor((region.X - output.X) * density),
                (int)Math.Floor((region.Y - output.Y) * density),
                (int)Math.Ceiling(region.Width * density),
                (int)Math.Ceiling(region.Height * density))
            .Intersect(new PixelRect(default, pixels));
    }

    private static Bitmap CropFrozenOutputs(IReadOnlyList<FrozenOutput> outputs, PixelRect bounds)
    {
        // A contained crop keeps native pixels one-to-one. Deriving scale from
        // rounded logical monitor dimensions subtly resamples and blurs text.
        var contained = outputs.FirstOrDefault(output => output.Bounds.Intersect(bounds) == bounds);
        if (contained is not null)
        {
            using var source = contained.Capture.ToSKImage() ?? throw new InvalidDataException("The frozen output has no pixels.");
            var clip = CapturePixelBounds(bounds, contained.Bounds, contained.Density, contained.Capture.Size);
            using var subset = source.Subset(new SKRectI(clip.X, clip.Y, clip.Right, clip.Bottom))
                ?? throw new InvalidDataException("The selected pixel region is empty.");
            using var croppedEncoding = subset.Encode(SKEncodedImageFormat.Png, 100);
            using var croppedStream = new MemoryStream(croppedEncoding.ToArray());
            return new Bitmap(croppedStream);
        }

        var density = outputs.Where(item => item.Bounds.Intersects(bounds))
            .Select(item => item.Density).DefaultIfEmpty(1).Max();
        var size = IVisualElementCapture.LimitOutputSize(new PixelSize(
            Math.Max(1, (int)Math.Ceiling(bounds.Width * density)),
            Math.Max(1, (int)Math.Ceiling(bounds.Height * density))));
        using var image = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(image);
        canvas.Clear(SKColors.Transparent);
        foreach (var output in outputs)
        {
            var region = output.Bounds.Intersect(bounds);
            if (region.Width <= 0 || region.Height <= 0)
            {
                continue;
            }

            using var source = output.Capture.ToSKImage();
            if (source is null)
            {
                continue;
            }

            var sourceRect = new SKRect(
                (float)((region.X - output.Bounds.X) * (double)source.Width / output.Bounds.Width),
                (float)((region.Y - output.Bounds.Y) * (double)source.Height / output.Bounds.Height),
                (float)((region.Right - output.Bounds.X) * (double)source.Width / output.Bounds.Width),
                (float)((region.Bottom - output.Bounds.Y) * (double)source.Height / output.Bounds.Height));
            var destination = new SKRect(
                (float)((region.X - bounds.X) * (double)size.Width / bounds.Width),
                (float)((region.Y - bounds.Y) * (double)size.Height / bounds.Height),
                (float)((region.Right - bounds.X) * (double)size.Width / bounds.Width),
                (float)((region.Bottom - bounds.Y) * (double)size.Height / bounds.Height));
            canvas.DrawImage(source, sourceRect, destination, new SKSamplingOptions(SKFilterMode.Linear), null);
        }

        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(encoded.ToArray());
        return new Bitmap(stream);
    }

    private sealed record FrozenOutput(PixelRect Bounds, IVisualElementCapture Capture, Bitmap Bitmap, double Density) : IDisposable
    {
        public void Dispose()
        {
            Bitmap.Dispose();
            Capture.Dispose();
        }
    }

}
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Everywhere.Automation;
using SkiaSharp;

namespace Everywhere.Extensions;

public static class VisualElementExtension
{
    extension(IVisualElementCapture capture)
    {
        /// <summary>
        /// Converts the captured bitmap capture into an Avalonia Bitmap object.
        /// </summary>
        /// <returns>Converted Bitmap if successful, or null if the pixel capture is empty.</returns>
        public Bitmap? ToAvaloniaBitmap()
        {
            var pixelSize = capture.Size;
            return pixelSize.Width <= 0 || pixelSize.Height <= 0 ?
                null :
                new Bitmap(
                    capture.Format,
                    capture.AlphaFormat,
                    capture.Data,
                    pixelSize,
                    new Vector(96, 96),
                    capture.Stride);
        }

        /// <summary>
        /// Converts the captured bitmap capture into a SkiaSharp SKImage object.
        /// </summary>
        /// <returns>Converted SKImage if successful, or null if the pixel capture is empty.</returns>
        /// <exception cref="ArgumentException"></exception>
        public SKImage? ToSKImage()
        {
            var pixelSize = capture.Size;
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;

            var info = new SKImageInfo(pixelSize.Width, pixelSize.Height, ToSkColorType(capture.Format), ToSkAlphaType(capture.AlphaFormat));
            using var skData = SKData.CreateCopy(capture.Data, capture.Stride * pixelSize.Height);
            return SKImage.FromPixels(info, skData, capture.Stride);

            static SKColorType ToSkColorType(PixelFormat fmt)
            {
                if (fmt == PixelFormat.Rgb565)
                    return SKColorType.Rgb565;
                if (fmt == PixelFormat.Bgra8888)
                    return SKColorType.Bgra8888;
                if (fmt == PixelFormat.Rgba8888)
                    return SKColorType.Rgba8888;
                if (fmt == PixelFormat.Rgb32)
                    return SKColorType.Rgb888x;
                throw new ArgumentException("Unknown pixel format: " + fmt);
            }

            static SKAlphaType ToSkAlphaType(AlphaFormat fmt)
            {
                return fmt switch
                {
                    AlphaFormat.Premul => SKAlphaType.Premul,
                    AlphaFormat.Unpremul => SKAlphaType.Unpremul,
                    AlphaFormat.Opaque => SKAlphaType.Opaque,
                    _ => throw new ArgumentException($"Unknown alpha format: {fmt}")
                };
            }
        }
    }
}
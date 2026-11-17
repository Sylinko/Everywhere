using SkiaSharp;

namespace Everywhere.Harness;

/// <summary>
/// A bounded still image prepared for model input.
/// </summary>
public sealed record ChatImage(
    byte[] Bytes,
    string MimeType,
    int Width,
    int Height,
    int OriginalWidth,
    int OriginalHeight
)
{
    public const int MaximumEncodedBytes = 4 * 1024 * 1024;
    private const int MaximumEdge = 1920;
    private const long MaximumDecodedPixels = 32_000_000;
    private const int MaximumInputFileBytes = 25 * 1024 * 1024;

    /// <summary>
    /// Checks the raster header before allocating pixels and validates decoding before returning.
    /// JPEG and PNG files that already fit retain their original bytes; other images are converted
    /// to a bounded still image.
    /// </summary>
    /// <exception cref="ImageLimitException">The file or its decoded dimensions exceed the input limits.</exception>
    /// <exception cref="InvalidDataException">The file is not a supported raster image or cannot be decoded.</exception>
    public static ChatImage Read(string path)
    {
        using var stream = File.OpenRead(path);
        var length = stream.Length;
        if (length > MaximumInputFileBytes)
        {
            throw new ImageLimitException("The image file exceeds 25 MiB. Use a smaller image or crop the relevant region.");
        }

        using var codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
        if (codec is null)
        {
            throw new InvalidDataException("The file is not a supported raster image.");
        }

        var info = codec.Info;
        // Encoded length alone does not bound native pixel allocation.
        if ((long)info.Width * info.Height > MaximumDecodedPixels)
        {
            throw new ImageLimitException("The image exceeds 32 million pixels. Resize it or crop the relevant region.");
        }

        var origin = codec.EncodedOrigin;
        var isTransposed = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var originalWidth = isTransposed ? info.Height : info.Width;
        var originalHeight = isTransposed ? info.Width : info.Height;
        var longestEdge = Math.Max(info.Width, info.Height);
        var mimeType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            _ => null
        };
        using var bitmap = new SKBitmap(info.WithColorType(SKColorType.Bgra8888).WithAlphaType(SKAlphaType.Premul));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result != SKCodecResult.Success)
        {
            throw new InvalidDataException($"The image could not be decoded: {result}.");
        }

        if (mimeType is not null && longestEdge <= MaximumEdge && length <= MaximumEncodedBytes)
        {
            stream.Position = 0;
            var bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
            return new ChatImage(bytes, mimeType, originalWidth, originalHeight, originalWidth, originalHeight);
        }

        var scale = Math.Min(1d, (double)MaximumEdge / longestEdge);
        var width = Math.Max(1, (int)(info.Width * scale));
        var height = Math.Max(1, (int)(info.Height * scale));
        using var preview = new SKBitmap(isTransposed ? height : width, isTransposed ? width : height);
        using var canvas = new SKCanvas(preview);
        var transform = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
            _ => SKMatrix.Identity
        };

        // Preserve crisp text and transparency in non-JPEG sources when PNG fits.
        if (codec.EncodedFormat != SKEncodedImageFormat.Jpeg)
        {
            Draw(SKColors.Transparent);
            using var png = preview.Encode(SKEncodedImageFormat.Png, 100);
            if (png is { Size: <= MaximumEncodedBytes })
            {
                return Create(png, "image/png");
            }
        }

        Draw(SKColors.White);
        using var jpeg = preview.Encode(SKEncodedImageFormat.Jpeg, 88);
        if (jpeg is not { Size: <= MaximumEncodedBytes })
        {
            throw new ImageLimitException("The prepared image exceeds 4 MiB. Use a smaller image or crop the relevant region.");
        }

        return Create(jpeg, "image/jpeg");

        void Draw(SKColor background)
        {
            canvas.Clear(background);
            canvas.SetMatrix(transform);
            canvas.DrawBitmap(bitmap, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
            canvas.Flush();
        }

        ChatImage Create(SKData data, string outputMimeType)
        {
            return new ChatImage(
                data.ToArray(),
                outputMimeType,
                preview.Width,
                preview.Height,
                originalWidth,
                originalHeight);
        }
    }

    /// <summary>
    /// Thrown when an image exceeds the preparation limits.
    /// </summary>
    public sealed class ImageLimitException(string message) : IOException(message);
}
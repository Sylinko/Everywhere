using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Everywhere.Media;
using Everywhere.Windows.Extensions;
using Everywhere.Windows.Interop;
using WinRT;
using ZLinq;
using OcrLine = Everywhere.Media.OcrLine;
using OcrResult = Everywhere.Media.OcrResult;

namespace Everywhere.Windows.Media;

public class WindowsOcrEngine : IOcrEngine
{
    private readonly OcrEngine? _engine = OcrEngine.TryCreateFromUserProfileLanguages();

    public bool IsSupported => _engine is not null;

    public async Task<OcrResult> RecognizeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_engine is null) throw new NotSupportedException("OCR is not supported on this system.");

        cancellationToken.ThrowIfCancellationRequested();
        using var stream = await FileRandomAccessStream.OpenAsync(filePath, FileAccessMode.Read);

        cancellationToken.ThrowIfCancellationRequested();
        var decoder = await BitmapDecoder.CreateAsync(stream);

        cancellationToken.ThrowIfCancellationRequested();
        using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            new BitmapTransform(),
            ExifOrientationMode.RespectExifOrientation,
            ColorManagementMode.DoNotColorManage);
        softwareBitmap.DpiX = decoder.DpiX;
        softwareBitmap.DpiY = decoder.DpiY;

        cancellationToken.ThrowIfCancellationRequested();
        return await PerformOcrAsync(_engine, softwareBitmap);
    }

    public async Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default)
    {
        if (_engine is null) throw new NotSupportedException("OCR is not supported on this system.");

        // create buffer and copy image bytes
        cancellationToken.ThrowIfCancellationRequested();
        var size = bitmap.PixelSize;
        using var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, size.Width, size.Height);
        using var buffer = softwareBitmap.LockBuffer(BitmapBufferAccessMode.Write);
        using var reference = buffer.CreateReference();

        cancellationToken.ThrowIfCancellationRequested();
        var byteAccess = reference.As<IMemoryBufferByteAccess>();
        unsafe
        {
            byteAccess.GetBuffer(out var pBuffer, out var capacity).ThrowOnFailure();
            if (pBuffer == null) throw new InvalidOperationException("Failed to get buffer pointer.");

            var plane = buffer.GetPlaneDescription(0);
            if (plane.Width != size.Width || plane.Height != size.Height)
                throw new InvalidOperationException("SoftwareBitmap plane size does not match bitmap size.");

            var minimumStride = checked(plane.Width * 4); // BGRA8 = 4 bytes per pixel.
            if (plane.StartIndex < 0 || plane.Stride < minimumStride)
                throw new InvalidOperationException("Unexpected SoftwareBitmap plane layout.");

            var planeBytes = (long)plane.Stride * plane.Height;
            var planeEnd = plane.StartIndex + planeBytes;
            if (planeEnd > capacity)
                throw new InvalidOperationException("SoftwareBitmap plane exceeds reported buffer capacity.");
            if (planeBytes > int.MaxValue)
                throw new NotSupportedException("Plane is too large for Avalonia Bitmap.CopyPixels bufferSize parameter.");

            var destination = (IntPtr)(pBuffer + plane.StartIndex);
            if (bitmap.Format == PixelFormat.Rgba8888 && bitmap.AlphaFormat == AlphaFormat.Premul)
            {
                // Fastest path: no alpha/pixel transcoding, just row copy into SoftwareBitmap.
                bitmap.CopyPixels(
                    new PixelRect(0, 0, size.Width, size.Height),
                    destination,
                    checked((int)planeBytes),
                    plane.Stride);
            }
            else
            {
                // General path: Avalonia transcode directly into SoftwareBitmap memory.
                bitmap.CopyPixels(new LockedFramebufferView(destination, size, plane.Stride, bitmap.Dpi), AlphaFormat.Premul);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await PerformOcrAsync(_engine, softwareBitmap);
    }

    private async static ValueTask<OcrResult> PerformOcrAsync(OcrEngine engine, SoftwareBitmap bitmap)
    {
        var result = await engine.RecognizeAsync(bitmap);
        return new OcrResult(
            result.Lines.AsValueEnumerable().Select(line => new OcrLine(
                line.Words.AsValueEnumerable().Select(word => new PixelRect(
                    (int)word.BoundingRect.X,
                    (int)word.BoundingRect.Y,
                    (int)word.BoundingRect.Width,
                    (int)word.BoundingRect.Height)).Aggregate((r1, r2) => r1.Union(r2)),
                line.Text)).ToList(),
            result.Text);
    }

    private sealed record LockedFramebufferView(
        nint Address,
        PixelSize Size,
        int RowBytes,
        Vector Dpi
    ) : ILockedFramebuffer
    {
        public PixelFormat Format => PixelFormat.Bgra8888;

        public void Dispose() { }
    }
}
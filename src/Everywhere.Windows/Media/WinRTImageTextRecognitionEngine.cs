using System.Diagnostics.CodeAnalysis;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Media.ImageRecognition;
using Everywhere.Windows.Extensions;
using Everywhere.Windows.Interop;
using Serilog;
using WinRT;
using ZLinq;

namespace Everywhere.Windows.Media;

public sealed class WinRTImageTextRecognitionEngine : IImageTextRecognitionEngine
{
    public string Id => "winrt";

    [MemberNotNullWhen(true, nameof(_availableLanguages))]
    public bool IsSupported => _availableLanguages is { Count: > 0 };

    public ImageTextRecognitionEngineDescriptor Descriptor { get; } = new(
        new DirectResourceKey("Windows OCR"),
        new DirectResourceKey(""),
        true,
        true);

    public IReadOnlyBindableList<DynamicNotification> Notifications { get; set; } = new BindableList<DynamicNotification>();

    public IReadOnlyList<LocaleName> SupportedLocales { get; }

    private readonly IReadOnlyList<Language>? _availableLanguages;

    public WinRTImageTextRecognitionEngine()
    {
        try
        {
            _availableLanguages = OcrEngine.AvailableRecognizerLanguages;
        }
        catch (Exception ex)
        {
            Log.ForContext<WinRTImageTextRecognitionEngine>().Error(ex, "Failed to get available OCR recognizer languages. OCR will be unavailable.");
        }

        if (_availableLanguages is { Count: > 0 })
        {
            SupportedLocales = _availableLanguages.AsValueEnumerable().Select(WinRTExtensions.ToLocaleName).ToList();
        }
        else
        {
            SupportedLocales = [];
        }
    }

    public async Task<ImageTextRecognitionResult> RecognizeAsync(string filePath, LocaleName locale, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) throw new NotSupportedException("OCR is not supported on this system.");

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
        return await PerformOcrAsync(locale, softwareBitmap);
    }

    public async Task<ImageTextRecognitionResult> RecognizeAsync(Bitmap bitmap, LocaleName locale, CancellationToken cancellationToken = default)
    {
        if (!IsSupported) throw new NotSupportedException("OCR is not supported on this system.");

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
        return await PerformOcrAsync(locale, softwareBitmap);
    }

    private async ValueTask<ImageTextRecognitionResult> PerformOcrAsync(LocaleName locale, SoftwareBitmap bitmap)
    {
        var engine = OcrEngine.TryCreateFromLanguage(locale.ToWinRTLanguage());
        if (engine is null)
        {
            var fallbackLanguage =
                _availableLanguages?.FirstOrDefault(l => l.ToLocaleName() == LocaleName.En) ??
                _availableLanguages?.FirstOrDefault();
            if (fallbackLanguage is null) throw new NotSupportedException("No available OCR languages found.");

            engine = OcrEngine.TryCreateFromLanguage(fallbackLanguage);
            if (engine is null) throw new NotSupportedException("OCR is not supported on this system.");
        }

        var result = await engine.RecognizeAsync(bitmap);
        return new ImageTextRecognitionResult(
            result.Lines.AsValueEnumerable().Select(line => new ImageTextRecognitionLine(
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
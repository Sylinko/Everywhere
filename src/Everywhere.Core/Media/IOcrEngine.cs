using Avalonia.Media.Imaging;

namespace Everywhere.Media;

public interface IOcrEngine
{
    bool IsSupported { get; }

    Task<OcrResult> RecognizeAsync(string filePath, CancellationToken cancellationToken = default);

    Task<OcrResult> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default);
}
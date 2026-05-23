using Avalonia.Media.Imaging;

namespace Everywhere.Media;

public interface IOcrEngine
{
    bool IsSupported { get; }

    IReadOnlyList<LocaleName> SupportedLocales { get; }

    Task<OcrResult> RecognizeAsync(string filePath, LocaleName locale, CancellationToken cancellationToken = default);

    Task<OcrResult> RecognizeAsync(Bitmap bitmap, LocaleName locale, CancellationToken cancellationToken = default);
}
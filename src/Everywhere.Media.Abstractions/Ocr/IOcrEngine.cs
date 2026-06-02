using Everywhere.I18N;

namespace Everywhere.Media.Ocr;

public interface IOcrEngine : IMediaEngine<OcrEngineDescriptor>
{
    Task<OcrResult> RecognizeAsync(string filePath, LocaleName locale, CancellationToken cancellationToken = default);

    // Task<OcrResult> RecognizeAsync(Bitmap bitmap, LocaleName locale, CancellationToken cancellationToken = default);
}

using Everywhere.I18N;

namespace Everywhere.Media.ImageRecognition;

public interface IImageTextRecognitionEngine : IMediaEngine<ImageTextRecognitionEngineDescriptor>
{
    Task<ImageTextRecognitionResult> RecognizeAsync(string filePath, LocaleName locale, CancellationToken cancellationToken = default);

    // Task<OcrResult> RecognizeAsync(Bitmap bitmap, LocaleName locale, CancellationToken cancellationToken = default);
}

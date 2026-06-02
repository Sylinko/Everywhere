using Everywhere.I18N;

namespace Everywhere.Media.SpeechRecognition;

public interface ISpeechRecognitionEngine : IMediaEngine<SpeechRecognitionEngineDescriptor>
{
    Task InitializeAsync();

    Task<ISpeechRecognitionSession> CreateSessionAsync(LocaleName locale, CancellationToken cancellationToken = default);
}

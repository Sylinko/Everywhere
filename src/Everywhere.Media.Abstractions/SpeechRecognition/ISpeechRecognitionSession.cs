using Everywhere.Media.Microphone;

namespace Everywhere.Media.SpeechRecognition;

/// <summary>
/// Represents a speech recognition session. The session can be used to recognize speech from an audio source, and to receive updates about the recognition process. The session should be disposed when it is no longer needed.
/// </summary>
public interface ISpeechRecognitionSession : IAsyncDisposable
{
    /// <summary>
    /// Completes the speech recognition session, releasing any resources associated with it. After calling this method, the session should not be used anymore.
    /// This method should be called when the recognition process is complete. e.g. User released the button to stop recording.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    ValueTask CompleteAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a speech recognition session that is hosted by the system.
/// This means that no audio frames are provided by the user, but instead the system will handle the audio input and provide updates about the recognition process.
/// </summary>
public interface ISystemHostedSpeechRecognitionSession : ISpeechRecognitionSession
{
    IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a speech recognition session that is hosted by the user.
/// This means that the user will provide audio frames to the session, and the session will provide updates about the recognition process. This is useful for scenarios where the user wants to use a custom audio source, e.g. a custom microphone capture implementation, or a pre-recorded audio file.
/// </summary>
public interface ICustomHostedSpeechRecognitionSession : ISpeechRecognitionSession
{
    IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync(IAsyncEnumerable<AudioFrame> input, CancellationToken cancellationToken = default);
}
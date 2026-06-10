using System.Runtime.CompilerServices;
using Everywhere.Media.Audio;
using SherpaOnnx;

namespace Everywhere.Media.SpeechRecognition.Sherpa;

public sealed class SherpaOnnxSpeechRecognitionSession(
    string? microphoneDeviceId,
    SherpaOnnxModelMetadata metadata,
    OnlineRecognizer recognizer
) : ICustomHostedSpeechRecognitionSession
{
    private OnlineStream? _stream;
    private string? _lastHypothesis;
    private string? _lastFinal;
    private int _isCompleted;
    private int _isDisposed;

    public MicrophoneCaptureOptions MicrophoneCaptureOptions => new(
        DeviceId: microphoneDeviceId,
        SampleRate: 16000,
        Channels: 1,
        FramesPerBuffer: 1600);

    public async IAsyncEnumerable<SpeechRecognitionUpdate> RecognizeAsync(
        IAsyncEnumerable<AudioFrame> input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        _stream = recognizer.CreateStream();
        yield return new SpeechRecognitionUpdate.Started();

        await foreach (var frame in input.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _isCompleted) != 0)
            {
                break;
            }

            var samples = AudioFrameConverter.ConvertToMono16Khz(frame);
            _stream.AcceptWaveform(metadata.RuntimeOptions.SampleRate, samples);

            foreach (var update in DecodeAvailable(finalizeEndpoint: true))
            {
                yield return update;
            }
        }

        foreach (var update in FinishStream())
        {
            yield return update;
        }

        yield return new SpeechRecognitionUpdate.Completed();
    }

    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _isCompleted, 1);

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return ValueTask.CompletedTask;

        _stream?.Dispose();
        recognizer.Dispose();
        return ValueTask.CompletedTask;
    }

    private IEnumerable<SpeechRecognitionUpdate> DecodeAvailable(bool finalizeEndpoint)
    {
        if (_stream is null) yield break;

        while (recognizer.IsReady(_stream))
        {
            recognizer.Decode(_stream);
        }

        var result = recognizer.GetResult(_stream).Text.Trim();
        if (!string.Equals(result, _lastHypothesis, StringComparison.Ordinal))
        {
            _lastHypothesis = result;
            if (!string.IsNullOrWhiteSpace(result))
            {
                yield return new SpeechRecognitionUpdate.Hypothesis(result);
            }
            else
            {
                yield return new SpeechRecognitionUpdate.Reset();
            }
        }

        if (!finalizeEndpoint || !recognizer.IsEndpoint(_stream)) yield break;

        foreach (var update in FinalizeCurrentSegment(result))
        {
            yield return update;
        }

        recognizer.Reset(_stream);
        _lastHypothesis = null;
    }

    private IEnumerable<SpeechRecognitionUpdate> FinishStream()
    {
        if (_stream is null) yield break;

        _stream.InputFinished();
        while (recognizer.IsReady(_stream))
        {
            recognizer.Decode(_stream);
        }

        var result = recognizer.GetResult(_stream).Text.Trim();
        foreach (var update in FinalizeCurrentSegment(result))
        {
            yield return update;
        }
    }

    private IEnumerable<SpeechRecognitionUpdate> FinalizeCurrentSegment(string text)
    {
        text = text.Trim();
        if (!string.IsNullOrWhiteSpace(text) && !string.Equals(text, _lastFinal, StringComparison.Ordinal))
        {
            _lastFinal = text;
            yield return new SpeechRecognitionUpdate.Final(text);
        }

        yield return new SpeechRecognitionUpdate.Reset();
    }
}

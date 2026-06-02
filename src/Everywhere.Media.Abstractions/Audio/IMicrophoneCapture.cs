namespace Everywhere.Media.Microphone;

public interface IMicrophoneCapture : IAsyncDisposable
{
    IAsyncEnumerable<AudioFrame> Frames { get; }

    Task StartAsync(
        MicrophoneCaptureOptions options,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
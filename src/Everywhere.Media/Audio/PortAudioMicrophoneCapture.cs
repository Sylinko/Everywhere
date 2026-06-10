using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PortAudioSharp;
using PaStream = PortAudioSharp.Stream;

namespace Everywhere.Media.Audio;

public sealed class PortAudioMicrophoneCapture(string? deviceId, ILogger logger) : IMicrophoneCapture
{
    private const int DefaultFramesPerBuffer = 1600;
    private const int QueueCapacity = 20;

    private readonly Channel<AudioFrame> _frames = Channel.CreateBounded<AudioFrame>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private PaStream? _stream;
    private MicrophoneCaptureOptions? _options;
    private Exception? _captureException;
    private long _droppedFrames;
    private int _isStarted;
    private int _isDisposed;

    public IAsyncEnumerable<AudioFrame> Frames => ReadFramesAsync();

    public Task StartAsync(MicrophoneCaptureOptions options, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_isDisposed != 0, this);
        if (Interlocked.Exchange(ref _isStarted, 1) != 0) return Task.CompletedTask;

        PortAudioRuntime.EnsureInitialized();

        var device = ResolveDevice(options.DeviceId ?? deviceId);
        if (device == PortAudio.NoDevice)
        {
            throw new InvalidOperationException("No default microphone device is available.");
        }

        var deviceInfo = PortAudio.GetDeviceInfo(device);
        var channels = options.Channels <= 0 ? 1 : Math.Min(options.Channels, Math.Max(1, deviceInfo.maxInputChannels));
        var sampleRate = options.SampleRate <= 0 ? 16000 : options.SampleRate;
        var framesPerBuffer = options.FramesPerBuffer > 0 ? options.FramesPerBuffer : DefaultFramesPerBuffer;
        _options = new MicrophoneCaptureOptions(DeviceId: device.ToString(), sampleRate, channels, framesPerBuffer);

        var inputParameters = new StreamParameters
        {
            device = device,
            channelCount = channels,
            sampleFormat = SampleFormat.Float32,
            suggestedLatency = deviceInfo.defaultLowInputLatency,
            hostApiSpecificStreamInfo = IntPtr.Zero
        };

        _stream = new PaStream(
            inputParameters,
            null,
            sampleRate,
            (uint)framesPerBuffer,
            StreamFlags.ClipOff,
            HandleAudioCallback,
            this);
        _stream.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isStarted, 0) == 0) return Task.CompletedTask;

        try
        {
            _stream?.Stop();
            _stream?.Close();
        }
        finally
        {
            _stream?.Dispose();
            _stream = null;
            _frames.Writer.TryComplete(_captureException);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<AudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    private StreamCallbackResult HandleAudioCallback(
        IntPtr input,
        IntPtr output,
        uint frameCount,
        ref StreamCallbackTimeInfo timeInfo,
        StreamCallbackFlags statusFlags,
        IntPtr userData)
    {
        if (_options is not { } options || input == IntPtr.Zero || Volatile.Read(ref _isStarted) == 0)
        {
            return StreamCallbackResult.Complete;
        }

        // ReSharper disable once BitwiseOperatorOnEnumWithoutFlags
        if ((statusFlags & StreamCallbackFlags.InputOverflow) != 0)
        {
            logger.LogDebug("PortAudio input overflowed.");
        }

        var sampleCount = checked((int)frameCount * options.Channels);
        var samples = new float[sampleCount];
        Marshal.Copy(input, samples, 0, sampleCount);

        var timestamp = TimeSpan.FromSeconds(timeInfo.inputBufferAdcTime);
        if (!_frames.Writer.TryWrite(new AudioFrame(options.SampleRate, options.Channels, samples, timestamp)))
        {
            var dropped = Interlocked.Increment(ref _droppedFrames);
            _captureException ??= new InvalidOperationException("Microphone audio queue is overloaded.");
            logger.LogWarning("PortAudio microphone queue overloaded. Dropped frame count: {DroppedFrames}.", dropped);
            return StreamCallbackResult.Abort;
        }

        return StreamCallbackResult.Continue;
    }

    private static int ResolveDevice(string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceId) && int.TryParse(deviceId, out var parsed))
        {
            return parsed;
        }

        return PortAudio.DefaultInputDevice;
    }
}

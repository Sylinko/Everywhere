namespace Everywhere.Media.Microphone;

public sealed record MicrophoneCaptureOptions(
    string? DeviceId,
    int SampleRate = 16000,
    int Channels = 1,
    int FramesPerBuffer = 0);
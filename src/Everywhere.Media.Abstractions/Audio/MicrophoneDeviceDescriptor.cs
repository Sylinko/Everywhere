namespace Everywhere.Media.Audio;

public sealed record MicrophoneDeviceDescriptor(
    string Id,
    string Name,
    int MaxInputChannels,
    int DefaultSampleRate,
    bool IsDefault);

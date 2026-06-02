namespace Everywhere.Media.Microphone;

public readonly record struct AudioFrame(
    int SampleRate,
    int Channels,
    ReadOnlyMemory<float> Samples,
    TimeSpan? Timestamp = null);
namespace Everywhere.Media.Audio;

public readonly record struct AudioFrame(
    int SampleRate,
    int Channels,
    ReadOnlyMemory<float> Samples,
    TimeSpan? Timestamp = null);
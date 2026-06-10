namespace Everywhere.Media.Audio;

public static class AudioFrameConverter
{
    public static float[] ConvertToMono16Khz(AudioFrame frame)
    {
        var mono = DownmixToMono(frame);
        return frame.SampleRate == 16000 ? mono : ResampleLinear(mono, frame.SampleRate, 16000);
    }

    private static float[] DownmixToMono(AudioFrame frame)
    {
        if (frame.Channels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "Audio frame channel count must be greater than zero.");
        }

        var source = frame.Samples.Span;
        if (frame.Channels == 1)
        {
            return source.ToArray();
        }

        var sampleCount = source.Length / frame.Channels;
        var result = new float[sampleCount];
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var sum = 0f;
            var baseIndex = sampleIndex * frame.Channels;
            for (var channel = 0; channel < frame.Channels; channel++)
            {
                sum += source[baseIndex + channel];
            }

            result[sampleIndex] = sum / frame.Channels;
        }

        return result;
    }

    private static float[] ResampleLinear(float[] source, int sourceSampleRate, int targetSampleRate)
    {
        if (sourceSampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceSampleRate));
        }

        if (source.Length == 0 || sourceSampleRate == targetSampleRate)
        {
            return source;
        }

        var targetLength = Math.Max(1, (int)Math.Round((double)source.Length * targetSampleRate / sourceSampleRate));
        var result = new float[targetLength];
        var ratio = (double)sourceSampleRate / targetSampleRate;
        for (var i = 0; i < result.Length; i++)
        {
            var sourcePosition = i * ratio;
            var left = (int)Math.Floor(sourcePosition);
            var right = Math.Min(left + 1, source.Length - 1);
            var t = (float)(sourcePosition - left);
            result[i] = source[left] + (source[right] - source[left]) * t;
        }

        return result;
    }
}

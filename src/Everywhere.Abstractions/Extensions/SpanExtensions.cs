namespace Everywhere.Extensions;

public static class SpanExtensions
{
    extension<T>(ReadOnlySpan<T> span)
    {
        public ReadOnlySpan<T> SafeSlice(int start, int length)
        {
            if (span.IsEmpty) return span;

            start = Math.Clamp(start, 0, span.Length - 1);
            length = Math.Clamp(length, 0, span.Length - start);
            return span.Slice(start, length);
        }
    }

    extension<T>(MemoryExtensions.SpanSplitEnumerator<T> enumerator) where T : IEquatable<T>
    {
        public Range FirstOrDefault(Range defaultValue = default)
        {
            foreach (var item in enumerator) return item;
            return defaultValue;
        }
    }
}
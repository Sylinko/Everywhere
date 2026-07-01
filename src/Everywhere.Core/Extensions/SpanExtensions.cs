namespace Everywhere.Extensions;

public static class SpanExtensions
{
    extension<T>(MemoryExtensions.SpanSplitEnumerator<T> enumerator) where T : IEquatable<T>
    {
        public Range FirstOrDefault(Range defaultValue = default)
        {
            foreach (var item in enumerator) return item;
            return defaultValue;
        }
    }
}
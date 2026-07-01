namespace Everywhere.Extensions;

public static class EnumerableExtensions
{
    /// <summary>
    /// Python: enumerator
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="enumerable"></param>
    /// <param name="startIndex"></param>
    /// <param name="step"></param>
    /// <returns></returns>
    public static IEnumerable<(int index, T item)> WithIndex<T>(
        this IEnumerable<T> enumerable,
        int startIndex = 0,
        int step = 1)
    {
        foreach (var item in enumerable)
        {
            yield return (startIndex, item);
            startIndex += step;
        }
    }

#if NET5_0_OR_GREATER
    public static Span<T> AsSpan<T>(this List<T> list) => CollectionsMarshal.AsSpan(list);

    public static ref TValue GetValue<TTKey, TValue>(this Dictionary<TTKey, TValue> dict, TTKey key) where TTKey : notnull => 
        ref CollectionsMarshal.GetValueRefOrNullRef(dict, key);
#endif

    public static IEnumerable<T> Reversed<T>(this IList<T> list)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            yield return list[i];
        }
    }

    public static IEnumerable<T> RepeatEach<T>(this IEnumerable<T> source, int extraTimes = 0)
    {
        foreach (var item in source)
        {
            for (var i = 0; i < extraTimes + 1; i++)
            {
                yield return item;
            }
        }
    }

    public static int FindIndexOf<T>(this IList<T> list, Predicate<T> predicate)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (predicate(list[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// 完全枚举一个 <see cref="IEnumerable"/>，并丢弃所有元素
    /// </summary>
    /// <param name="enumerable"></param>
    [MethodImpl(MethodImplOptions.NoOptimization)]
    public static void Discard(this IEnumerable enumerable)
    {
        foreach (var _ in enumerable)
        { }
    }

    /// <summary>
    /// 完全枚举一个 <see cref="IEnumerable{T}"/>，并丢弃所有元素
    /// </summary>
    /// <param name="enumerable"></param>
    /// <typeparam name="T"></typeparam>
    [MethodImpl(MethodImplOptions.NoOptimization)]
    public static void Discard<T>(this IEnumerable<T> enumerable)
    {
        foreach (var _ in enumerable)
        { }
    }

    public static IEnumerable<T> Invoke<T>(this IEnumerable<T> source, Action<T> action)
    {
        foreach (var item in source)
        {
            action(item);
            yield return item;
        }
    }

    public static IEnumerable<(T? previous, T current)> PreviousAndCurrent<T>(
        this IEnumerable<T> source,
        T? previous = default)
    {
        foreach (var current in source)
        {
            yield return (previous, current);
            previous = current;
        }
    }
    
    public static IEnumerable<(T Current, T Next)> CurrentAndNext<T>(this IEnumerable<T> source)
    {
        using var enumerator = source.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            yield break;
        }

        var previous = enumerator.Current;
        while (enumerator.MoveNext())
        {
            yield return (previous, enumerator.Current);
            previous = enumerator.Current;
        }
    }
    
    private class AnonymousEnumerable<T>(IEnumerator<T> enumerator) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            return enumerator;
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return enumerator;
        }
    }

    public static IEnumerable<T> AsEnumerable<T>(this IEnumerator<T> enumerator)
    {
        return new AnonymousEnumerable<T>(enumerator);
    }
    
    public static async ValueTask<T> FirstAsync<T>(this IAsyncEnumerable<T> enumerable)
    {
        await foreach (var item in enumerable)
        {
            return item;
        }

        throw new InvalidOperationException("Sequence contains no elements");
    }
    
    public static async IAsyncEnumerable<(int index, T item)> WithIndexAsync<T>(
        this IAsyncEnumerable<T> enumerable,
        int startIndex = 0,
        int step = 1)
    {
        var index = startIndex;
        await foreach (var item in enumerable)
        {
            yield return (index, item);
            index += step;
        }
    }

    public static IEnumerable<double> CompareEach(this IEnumerable<double> source)
    {
        double? last = null;
        foreach (var value in source)
        {
            if (last != null)
            {
                yield return value - last.Value;
            }
            last = value;
        }
    }

    public static int FindIndexOf<T>(this IEnumerable<T> source, Predicate<T> predicate)
    {
        var index = 0;
        foreach (var item in source)
        {
            if (predicate(item))
            {
                return index;
            }

            index++;
        }

        return -1;
    }

    public static IEnumerable<T> Generate<T>(Func<T> generator, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return generator();
        }
    }
    
    public static IEnumerable<T> Generate<T>(Func<int, T> generator, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return generator(i);
        }
    }
    
    public static void RemoveRange<T>(this IList<T> list, int index, int count)
    {
        // Delete from back to front to avoid frequent element shifting
        for (var i = index + count - 1; i >= index; i--)
        {
            list.RemoveAt(i);
        }
    }

    /// <summary>
    /// Throws if the cancellation token is cancelled during enumeration
    /// </summary>
    /// <param name="source"></param>
    /// <param name="cancellationToken"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IEnumerable<T> WithCancellation<T>(this IEnumerable<T> source, CancellationToken cancellationToken)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
        }
    }
}
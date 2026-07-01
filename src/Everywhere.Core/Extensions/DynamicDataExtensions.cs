using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DynamicData;
using Everywhere.Collections;

namespace Everywhere.Extensions;

public static class DynamicDataExtensions
{
    /// <summary>
    /// A convenience method to add the disposable to a collection.
    /// </summary>
    /// <param name="disposable"></param>
    /// <param name="disposables"></param>
    public static void AddTo(this IDisposable disposable, ICollection<IDisposable> disposables) => disposables.Add(disposable);

    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    extension<T>(ISourceList<T> source) where T : notnull
    {
        /// <summary>
        /// Clears the source list and adds the new data.
        /// </summary>
        /// <param name="data"></param>
        public void Reset(IEnumerable<T> data)
        {
            source.Edit(list =>
            {
                list.Clear();
                list.AddRange(data);
            });
        }

        /// <summary>
        /// Gets the index of the specified item in the source list.
        /// </summary>
        /// <param name="item"></param>
        /// <returns></returns>
        public int IndexOf(T item)
        {
            var index = 0;
            source.Edit(list => index = list.IndexOf(item));
            return index;
        }

        public T ItemAt(int index)
        {
            T? item = default;
            source.Edit(list => item = list[index]);
            return item!;
        }

        public int Count(Func<T, bool> predicate)
        {
            var count = 0;
            source.Edit(list => count = list.Count(predicate));
            return count;
        }
    }

    /// <summary>
    /// Clears the source list and adds the new data.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    public static void Reset<T>(this IExtendedList<T> source, IEnumerable<T> data)
    {
        source.Clear();
        source.AddRange(data);
    }

    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    extension<T>(IObservable<IChangeSet<T>> source) where T : notnull
    {
        /// <summary>
        /// A convenience method to revert the out parameter pattern of Bind method.
        /// </summary>
        /// <param name="disposable"></param>
        /// <param name="resetThreshold"></param>
        /// <returns></returns>
        public IReadOnlyBindableList<T> BindEx(
            out IDisposable disposable,
            int resetThreshold = 25)
        {
            disposable = source.Bind(out var collection, resetThreshold).Subscribe();
            return collection.ToReadOnlyBindableList();
        }

        /// <summary>
        /// A convenience method to add the subscription disposable to a collection.
        /// </summary>
        /// <param name="disposables"></param>
        /// <param name="resetThreshold"></param>
        /// <returns></returns>
        public IReadOnlyBindableList<T> BindEx(
            ICollection<IDisposable> disposables,
            int resetThreshold = 25)
        {
            var subscription = source.Bind(out var collection, resetThreshold).Subscribe();
            disposables.Add(subscription);
            return collection.ToReadOnlyBindableList();
        }
    }

    extension<TValue, TKey>(IObservable<IChangeSet<TValue, TKey>> source) where TValue : notnull where TKey : notnull
    {
        /// <summary>
        /// A convenience method to revert the out parameter pattern of Bind method.
        /// </summary>
        /// <param name="disposable"></param>
        /// <param name="resetThreshold"></param>
        /// <returns></returns>
        public IReadOnlyBindableList<TValue> BindEx(
            out IDisposable disposable,
            int resetThreshold = 25)
        {
            disposable = source.Bind(out var collection, resetThreshold).Subscribe();
            return collection.ToReadOnlyBindableList();
        }

        /// <summary>
        /// A convenience method to add the subscription disposable to a collection.
        /// </summary>
        /// <param name="disposables"></param>
        /// <param name="resetThreshold"></param>
        /// <returns></returns>
        public IReadOnlyBindableList<TValue> BindEx(
            ICollection<IDisposable> disposables,
            int resetThreshold = 25)
        {
            var subscription = source.Bind(out var collection, resetThreshold).Subscribe();
            disposables.Add(subscription);
            return collection.ToReadOnlyBindableList();
        }
    }

    /// <summary>
    /// Leading and tailing throttle
    /// </summary>
    /// <param name="source"></param>
    /// <param name="delay"></param>
    /// <param name="scheduler"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IObservable<T> ThrottleWithLeadingEdge<T>(
        this IObservable<T> source,
        TimeSpan delay,
        IScheduler? scheduler = null)
    {
        scheduler ??= DefaultScheduler.Instance;

        return source.Publish(shared =>
        {
            var debounce = shared.Throttle(delay, scheduler);
            var leading = shared.Window(() => debounce).SelectMany(w => w.TakeLast(1));
            return leading.Merge(debounce).DistinctUntilChanged();
        });
    }
}

using System.Collections.ObjectModel;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using DynamicData;

namespace Everywhere.Extensions;

public static class DynamicDataExtensions
{
    /// <summary>
    /// A convenience method to add the disposable to a collection.
    /// </summary>
    /// <param name="disposable"></param>
    /// <param name="disposables"></param>
    public static void AddTo(this IDisposable disposable, ICollection<IDisposable> disposables) => disposables.Add(disposable);

    /// <summary>
    /// Clears the source list and adds the new data.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="data"></param>
    /// <typeparam name="T"></typeparam>
    public static void Reset<T>(this ISourceList<T> source, IEnumerable<T> data) where T : notnull
    {
        source.Edit(list =>
        {
            list.Clear();
            list.AddRange(data);
        });
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
        public ReadOnlyObservableCollection<T> BindEx(
            out IDisposable disposable,
            int resetThreshold = 25)
        {
            disposable = source.Bind(out var collection, resetThreshold).Subscribe();
            return collection;
        }

        /// <summary>
        /// A convenience method to add the subscription disposable to a collection.
        /// </summary>
        /// <param name="disposables"></param>
        /// <param name="resetThreshold"></param>
        /// <returns></returns>
        public ReadOnlyObservableCollection<T> BindEx(
            ICollection<IDisposable> disposables,
            int resetThreshold = 25)
        {
            var subscription = source.Bind(out var collection, resetThreshold).Subscribe();
            disposables.Add(subscription);
            return collection;
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
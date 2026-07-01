using System.Collections.Specialized;
using System.ComponentModel;

namespace Everywhere.Collections;

/// <summary>
/// Represents a read-only list of elements that notifies listeners of dynamic changes, such as when items get added, removed, or when the whole list is refreshed.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IReadOnlyBindableList<out T> : IReadOnlyList<T>, INotifyCollectionChanged, INotifyPropertyChanged;

public interface IObservableListSource<T> : IDisposable
{
    IReadOnlyBindableList<T> View { get; }

    void Edit(Action<IListEditor<T>> edit);
}

public interface IListEditor<in T>
{
    void Add(T item);

    void AddRange(IEnumerable<T> items);

    bool Remove(T item);

    void Clear();
}

public interface IObservableCacheSource<T, in TKey> : IDisposable where TKey : notnull
{
    IReadOnlyBindableList<T> View { get; }

    void Edit(Action<ICacheEditor<T, TKey>> edit);
}

public interface ICacheEditor<in T, in TKey> where TKey : notnull
{
    void AddOrUpdate(T item);

    void AddOrUpdate(IEnumerable<T> items);

    bool RemoveKey(TKey key);

    void RemoveKeys(IEnumerable<TKey> keys);

    void Clear();
}

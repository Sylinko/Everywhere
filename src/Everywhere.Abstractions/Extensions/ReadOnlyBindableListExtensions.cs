using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using DynamicData;
using Everywhere.Collections;
using ObservableCollections;

namespace Everywhere.Extensions;

public static class ReadOnlyBindableListExtensions
{
    /// <summary>
    /// Converts a ObservableCollection{T} to an IReadOnlyObservableList{T} while preserving the collection change notifications.
    /// </summary>
    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IReadOnlyBindableList<T> ToReadOnlyBindableList<T>(this ObservableCollection<T> source) =>
        new ReadOnlyBindableListAdapter<T>(source);

    /// <summary>
    /// Converts a ReadOnlyObservableCollection{T} to an IReadOnlyObservableList{T} while preserving the collection change notifications.
    /// </summary>
    /// <param name="source"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IReadOnlyBindableList<T> ToReadOnlyBindableList<T>(this ReadOnlyObservableCollection<T> source) =>
        new ReadOnlyBindableListAdapter<T>(source);

    /// <summary>
    /// Converts an INotifyCollectionChangedSynchronizedViewList{T} to an IReadOnlyObservableList{T} while preserving the collection change notifications.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="disposable"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IReadOnlyBindableList<T> ToReadOnlyBindableList<T>(
        this INotifyCollectionChangedSynchronizedViewList<T> source,
        out IDisposable disposable)
    {
        disposable = source;
        return new ReadOnlyBindableListAdapter<T>(source);
    }

    /// <summary>
    /// Converts an INotifyCollectionChangedSynchronizedViewList{T} to an IReadOnlyObservableList{T} and registers the view lifetime with the owner.
    /// </summary>
    /// <param name="source"></param>
    /// <param name="disposables"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static IReadOnlyBindableList<T> ToReadOnlyBindableList<T>(
        this INotifyCollectionChangedSynchronizedViewList<T> source,
        ICollection<IDisposable> disposables)
    {
        disposables.Add(source);
        return new ReadOnlyBindableListAdapter<T>(source);
    }

    private sealed class ReadOnlyBindableListAdapter<T>(IReadOnlyList<T> inner) : IReadOnlyBindableList<T>, IList<T>, IList
    {
        public int Count => inner.Count;

        bool IList.IsFixedSize => true;
        bool IList.IsReadOnly => true;
        bool ICollection<T>.IsReadOnly => true;
        bool ICollection.IsSynchronized => false;
        object ICollection.SyncRoot => this;

        object? IList.this[int index]
        {
            get => inner[index];
            set => throw new NotSupportedException("This collection is read-only.");
        }

        T IList<T>.this[int index]
        {
            get => inner[index];
            set => throw new NotSupportedException("This collection is read-only.");
        }

        public T this[int index] => inner[index];

        public event NotifyCollectionChangedEventHandler? CollectionChanged
        {
            add => ((INotifyCollectionChanged)inner).CollectionChanged += value;
            remove => ((INotifyCollectionChanged)inner).CollectionChanged -= value;
        }

        public event PropertyChangedEventHandler? PropertyChanged
        {
            add => ((INotifyPropertyChanged)inner).PropertyChanged += value;
            remove => ((INotifyPropertyChanged)inner).PropertyChanged -= value;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public IEnumerator<T> GetEnumerator() => inner.GetEnumerator();

        bool IList.Contains(object? value) => value is T t && inner.Contains(t);

        bool ICollection<T>.Contains(T item) => inner.Contains(item);

        int IList.IndexOf(object? value) => value is T t ? ((IList<T>)this).IndexOf(t) : -1;

        int IList<T>.IndexOf(T item) => inner switch
        {
            IList<T> list => list.IndexOf(item),
            _ => inner.IndexOf(item)
        };

        void ICollection.CopyTo(Array array, int index)
        {
            switch (inner)
            {
                case ICollection collection:
                {
                    collection.CopyTo(array, index);
                    break;
                }
                default:
                {
                    if (array is T[] typedArray)
                    {
                        ((ICollection<T>)this).CopyTo(typedArray, index);
                    }
                    else
                    {
                        for (var i = 0; i < inner.Count; i++)
                        {
                            array.SetValue(inner[i], index + i);
                        }
                    }
                    break;
                }
            }
        }

        void ICollection<T>.CopyTo(T[] array, int arrayIndex)
        {
            switch (inner)
            {
                case ICollection<T> collection:
                {
                    collection.CopyTo(array, arrayIndex);
                    break;
                }
                default:
                {
                    for (var i = 0; i < inner.Count; i++)
                    {
                        array[arrayIndex + i] = inner[i];
                    }
                    break;
                }
            }
        }

        int IList.Add(object? value) => throw new NotSupportedException("This collection is read-only.");

        void ICollection<T>.Add(T item) => throw new NotSupportedException("This collection is read-only.");

        void IList.Clear() => throw new NotSupportedException("This collection is read-only.");

        void ICollection<T>.Clear() => throw new NotSupportedException("This collection is read-only.");

        void IList.Insert(int index, object? value) => throw new NotSupportedException("This collection is read-only.");

        void IList<T>.Insert(int index, T item) => throw new NotSupportedException("This collection is read-only.");

        void IList.Remove(object? value) => throw new NotSupportedException("This collection is read-only.");

        bool ICollection<T>.Remove(T item) => throw new NotSupportedException("This collection is read-only.");

        void IList.RemoveAt(int index) => throw new NotSupportedException("This collection is read-only.");

        void IList<T>.RemoveAt(int index) => throw new NotSupportedException("This collection is read-only.");
    }
}

using System.Collections.Immutable;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Common;

namespace Everywhere.Collections;

public interface IObservableDictionary : IDictionary, INotifyPropertyChanged, INotifyCollectionChanged;

public interface IKeyValuePair
{
    object? Key { get; }
    object? Value { get; }
}

public readonly struct DictionaryEventItem<TKey, TValue>(TKey key, TValue value) : IKeyValuePair
{
    public object? Key => key;
    public object? Value => value;
}

file static class PropertyChangedEventCache
{
    public static readonly PropertyChangedEventArgs CountPropertyChanged = new(nameof(ICollection.Count));
    public static readonly PropertyChangedEventArgs KeysPropertyChanged = new(nameof(IDictionary.Keys));
    public static readonly PropertyChangedEventArgs ValuesPropertyChanged = new(nameof(IDictionary.Values));
    public static readonly PropertyChangedEventArgs IndexerPropertyChanged = new("Item[]");
}

public class ObservableDictionary<TKey, TValue> :
    IObservableDictionary,
    IDictionary<TKey, TValue>,
    IReadOnlyDictionary<TKey, TValue>,
    ICollection<ObservableKeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    public object SyncRoot { get; } = new();

    public bool IsFixedSize => false;

    public bool IsSynchronized => true;

    public bool IsReadOnly => false;

    public int Count
    {
        get
        {
            lock (SyncRoot)
            {
                return _dictionary.Count;
            }
        }
    }

    ICollection IDictionary.Keys
    {
        get
        {
            lock (SyncRoot)
            {
                return ((IDictionary)_dictionary).Keys;
            }
        }
    }

    public ICollection<TKey> Keys
    {
        get
        {
            lock (SyncRoot)
            {
                return _dictionary.Keys;
            }
        }
    }

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    ICollection IDictionary.Values
    {
        get
        {
            lock (SyncRoot)
            {
                return ((IDictionary)_dictionary).Values;
            }
        }
    }

    public ICollection<TValue> Values
    {
        get
        {
            lock (SyncRoot)
            {
                return _dictionary.Values;
            }
        }
    }

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public object? this[object key]
    {
        get
        {
            lock (SyncRoot)
            {
                return ((IDictionary)_dictionary)[key];
            }
        }
        set
        {
            lock (SyncRoot)
            {
                ((IDictionary)_dictionary)[key] = value;
            }
        }
    }

    public TValue this[TKey key]
    {
        get
        {
            lock (SyncRoot)
            {
                return _dictionary[key];
            }
        }
        set
        {
            lock (SyncRoot)
            {
                if (_dictionary.TryGetValue(key, out var oldValue))
                {
                    _dictionary[key] = value;
                    OnCollectionChanged(
                        new NotifyCollectionChangedEventArgs(
                            NotifyCollectionChangedAction.Replace,
                            new DictionaryEventItem<TKey, TValue>(key, value),
                            new DictionaryEventItem<TKey, TValue>(key, oldValue)));
                    OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
                    OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
                }
                else
                {
                    Add(key, value);
                }
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        PropertyChanged?.Invoke(this, e);
    }

    private void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        CollectionChanged?.Invoke(this, e);
    }

    private readonly Dictionary<TKey, TValue> _dictionary;

    public ObservableDictionary()
    {
        _dictionary = new Dictionary<TKey, TValue>();
    }

    public ObservableDictionary(IEqualityComparer<TKey>? comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(comparer: comparer);
    }

    public ObservableDictionary(int capacity, IEqualityComparer<TKey>? comparer)
    {
        _dictionary = new Dictionary<TKey, TValue>(capacity, comparer: comparer);
    }

    public ObservableDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer = null)
    {
        _dictionary = new Dictionary<TKey, TValue>(collection: collection, comparer: comparer);
    }

    public void Add(object key, object? value)
    {
        lock (SyncRoot)
        {
            ((IDictionary)_dictionary).Add(key, value);
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add,
                    new DictionaryEventItem<TKey, TValue>((TKey)key, (TValue)value!)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
        }
    }

    public void Add(TKey key, TValue value)
    {
        lock (SyncRoot)
        {
            _dictionary.Add(key, value);
            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Add,
                    new DictionaryEventItem<TKey, TValue>(key, value)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
        }
    }

    public void Add(KeyValuePair<TKey, TValue> item)
    {
        Add(item.Key, item.Value);
    }

    public void Add(ObservableKeyValuePair<TKey, TValue> item)
    {
        this[item.Key] = item.Value;
    }

    public void CopyTo(Array array, int index)
    {
        lock (SyncRoot)
        {
            ((IDictionary)_dictionary).CopyTo(array, index);
        }
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        lock (SyncRoot)
        {
            ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, arrayIndex);
        }
    }

    public void CopyTo(ObservableKeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        lock (SyncRoot)
        {
            var i = arrayIndex;
            foreach (var kvp in _dictionary)
            {
                array[i++] = new ObservableKeyValuePair<TKey, TValue>(kvp.Key, kvp.Value);
            }
        }
    }

    public bool Remove(ObservableKeyValuePair<TKey, TValue> item)
    {
        lock (SyncRoot)
        {
            if (!_dictionary.TryGetValue(item.Key, out var value) ||
                !EqualityComparer<TValue>.Default.Equals(value, item.Value) ||
                !_dictionary.Remove(item.Key, out var value2)) return false;

            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove,
                    new DictionaryEventItem<TKey, TValue>(item.Key, value2)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
            return true;
        }
    }

    public void Clear()
    {
        lock (SyncRoot)
        {
            if (_dictionary.Count <= 0) return;

            _dictionary.Clear();
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
        }
    }

    public bool Contains(object key)
    {
        lock (SyncRoot)
        {
            return ((IDictionary)_dictionary).Contains(key);
        }
    }

    public bool Contains(KeyValuePair<TKey, TValue> item)
    {
        lock (SyncRoot)
        {
            return ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).Contains(item);
        }
    }

    public bool Contains(ObservableKeyValuePair<TKey, TValue> item)
    {
        lock (SyncRoot)
        {
            return _dictionary.TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);
        }
    }

    public bool ContainsKey(TKey key)
    {
        lock (SyncRoot)
        {
            return ((IDictionary<TKey, TValue>)_dictionary).ContainsKey(key);
        }
    }

    IDictionaryEnumerator IDictionary.GetEnumerator()
    {
        Dictionary<TKey,TValue> snapshot;

        lock (SyncRoot)
        {
            snapshot = _dictionary.ToDictionary();
        }

        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        lock (SyncRoot)
        {
            return _dictionary.ToList().GetEnumerator();
        }
    }

    IEnumerator<ObservableKeyValuePair<TKey, TValue>> IEnumerable<ObservableKeyValuePair<TKey, TValue>>.GetEnumerator()
    {
        List<KeyValuePair<TKey, TValue>> snapshot;

        lock (SyncRoot)
        {
            snapshot = [.. _dictionary];
        }

        foreach (var kvp in snapshot)
        {
            yield return new ObservableKeyValuePair<TKey, TValue>(kvp.Key, kvp.Value);
        }
    }

    public void Remove(object key)
    {
        lock (SyncRoot)
        {
            if (!_dictionary.Remove((TKey)key, out var value)) return;

            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove,
                    new DictionaryEventItem<TKey, TValue>((TKey)key, value)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
        }
    }

    public bool Remove(TKey key)
    {
        lock (SyncRoot)
        {
            if (!_dictionary.Remove(key, out var value)) return false;

            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove,
                    new DictionaryEventItem<TKey, TValue>(key, value)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
            return true;
        }
    }

    public bool Remove(KeyValuePair<TKey, TValue> item)
    {
        lock (SyncRoot)
        {
            if (!_dictionary.TryGetValue(item.Key, out var value) ||
                !EqualityComparer<TValue>.Default.Equals(value, item.Value) ||
                !_dictionary.Remove(item.Key, out var value2)) return false;

            OnCollectionChanged(
                new NotifyCollectionChangedEventArgs(
                    NotifyCollectionChangedAction.Remove,
                    new DictionaryEventItem<TKey, TValue>(item.Key, value2)));
            OnPropertyChanged(PropertyChangedEventCache.CountPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.IndexerPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.KeysPropertyChanged);
            OnPropertyChanged(PropertyChangedEventCache.ValuesPropertyChanged);
            return true;
        }
    }

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value)
    {
        lock (SyncRoot)
        {
            return _dictionary.TryGetValue(key, out value);
        }
    }
}

/// <summary>
/// Only can create with initial items, modifies takes no effect. Used for configuration & observable scenario
/// </summary>
/// <typeparam name="TKey"></typeparam>
/// <typeparam name="TValue"></typeparam>
public sealed class ObservableImmutableDictionary<TKey, TValue> :
    IObservableDictionary,
    IDictionary<TKey, TValue>,
    IReadOnlyDictionary<TKey, TValue>,
    ICollection<ObservableKeyValuePair<TKey, TValue>>
    where TKey : notnull
{
    public object SyncRoot { get; } = new();

    public bool IsFixedSize => true;

    public bool IsSynchronized => true;

    public bool IsReadOnly => true;

    public int Count => _dictionary.Count;

    ICollection IDictionary.Keys => ((IDictionary)_dictionary).Keys;

    public ICollection<TKey> Keys => ((IDictionary<TKey, TValue>)_dictionary).Keys;

    IEnumerable<TKey> IReadOnlyDictionary<TKey, TValue>.Keys => Keys;

    ICollection IDictionary.Values => ((IDictionary)_dictionary).Values;

    public ICollection<TValue> Values => ((IDictionary<TKey, TValue>)_dictionary).Values;

    IEnumerable<TValue> IReadOnlyDictionary<TKey, TValue>.Values => Values;

    public object? this[object key]
    {
        get => ((IDictionary)_dictionary)[key];
        set { } // do nothing
    }

    public TValue this[TKey key]
    {
        get => _dictionary[key];
        set { } // do nothing
    }

    #pragma warning disable CS0067
    public event PropertyChangedEventHandler? PropertyChanged;
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    #pragma warning restore CS0067

    private readonly ImmutableDictionary<TKey, TValue> _dictionary;

    public ObservableImmutableDictionary(IEnumerable<KeyValuePair<TKey, TValue>> collection, IEqualityComparer<TKey>? comparer = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, TValue>(comparer);
        builder.AddRange(collection);
        _dictionary = builder.ToImmutable();
    }

    public void Add(object key, object? value) { }

    public void Add(TKey key, TValue value) { }

    public void Add(KeyValuePair<TKey, TValue> item) { }

    public void Add(ObservableKeyValuePair<TKey, TValue> item) { }

    public void CopyTo(Array array, int index)
    {
        ((IDictionary)_dictionary).CopyTo(array, index);
    }

    public void CopyTo(KeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        ((ICollection<KeyValuePair<TKey, TValue>>)_dictionary).CopyTo(array, arrayIndex);
    }

    public void CopyTo(ObservableKeyValuePair<TKey, TValue>[] array, int arrayIndex)
    {
        var i = arrayIndex;
        foreach (var kvp in _dictionary)
        {
            array[i++] = new ObservableKeyValuePair<TKey, TValue>(kvp.Key, kvp.Value);
        }
    }

    public bool Remove(ObservableKeyValuePair<TKey, TValue> item) => false;

    public void Clear() { }

    public bool Contains(object key) => ((IDictionary)_dictionary).Contains(key);

    public bool Contains(KeyValuePair<TKey, TValue> item) => _dictionary.Contains(item);

    public bool Contains(ObservableKeyValuePair<TKey, TValue> item) =>
        _dictionary.TryGetValue(item.Key, out var value) && EqualityComparer<TValue>.Default.Equals(value, item.Value);

    public bool ContainsKey(TKey key) => _dictionary.ContainsKey(key);

    IDictionaryEnumerator IDictionary.GetEnumerator() => ((IDictionary)_dictionary).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => ((IDictionary)_dictionary).GetEnumerator();

    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator() => _dictionary.GetEnumerator();

    IEnumerator<ObservableKeyValuePair<TKey, TValue>> IEnumerable<ObservableKeyValuePair<TKey, TValue>>.GetEnumerator()
    {
        foreach (var kvp in _dictionary)
        {
            yield return new ObservableKeyValuePair<TKey, TValue>(kvp.Key, kvp.Value);
        }
    }

    public void Remove(object key) { }

    public bool Remove(TKey key) => false;

    public bool Remove(KeyValuePair<TKey, TValue> item) => false;

    public bool TryGetValue(TKey key, [MaybeNullWhen(false)] out TValue value) => _dictionary.TryGetValue(key, out value);
}
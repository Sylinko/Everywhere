using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables;
using System.Reflection;
using System.Text.Json.Serialization;
using Everywhere.Collections;
using ZLinq;

namespace Everywhere.Utilities;

public readonly record struct ObjectObserverChangedEventArgs(string Path, object? Value);

public delegate void ObjectObserverChangedEventHandler(in ObjectObserverChangedEventArgs e);

[AttributeUsage(AttributeTargets.Property)]
public class ObjectObserverIgnoreAttribute : Attribute;

/// <summary>
/// Observes an INotifyPropertyChanged and its properties for changes.
/// Supports nested objects and collections.
/// </summary>
public class ObjectObserver(ObjectObserverChangedEventHandler handler) : IDisposable
{
    private readonly ConcurrentDictionary<Type, IReadOnlyList<PropertyInfo>> _cachedProperties = [];

    private IReadOnlyList<PropertyInfo> GetPropertyInfos(Type type) =>
        _cachedProperties.GetOrAdd(
            type,
            t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .AsValueEnumerable()
                .Where(p =>
                    p is { CanRead: true, CanWrite: true, IsSpecialName: false } ||
                    p.PropertyType.IsAssignableTo(typeof(INotifyPropertyChanged)))
                .Where(p => p.GetMethod?.GetParameters() is { Length: 0 }) // Ignore
                .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
                .Where(p => p.GetCustomAttribute<ObjectObserverIgnoreAttribute>() is null)
                .ToList());

    private PropertyInfo? GetPropertyInfo(Type type, string propertyName) =>
        GetPropertyInfos(type).AsValueEnumerable().FirstOrDefault(p => p.Name == propertyName);

    private readonly ObjectObserverChangedEventHandler _handler = handler;
    private readonly CompositeDisposable _observations = new();

    ~ObjectObserver()
    {
        Dispose();
    }

    public ObjectObserver Observe(
        [DynamicallyAccessedMembers(
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicFields |
            DynamicallyAccessedMemberTypes.PublicProperties)]
        INotifyPropertyChanged target,
        string basePath = "")
    {
        _observations.Add(new Observation(basePath, target, this));
        return this;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        _observations.Dispose();
    }

    private class Observation : IDisposable
    {
        private readonly string _basePath;
        private readonly Type _targetType;
        private readonly ObjectObserver _observer;
        private readonly WeakReference<INotifyPropertyChanged> _targetReference;
        private readonly ConcurrentDictionary<string, Observation> _observations = [];

        /// <summary>
        /// when <see cref="ObservableCollection{T}"/> is Reset, we cannot get the old items count from event args.
        /// So we need to keep track of the count ourselves.
        /// </summary>
        private int _listItemCount;
        private bool _isDisposed;

        public Observation(string basePath, INotifyPropertyChanged target, ObjectObserver observer)
        {
            _basePath = (basePath + ':').TrimStart(':');
            _targetType = target.GetType();
            _observer = observer;
            _targetReference = new WeakReference<INotifyPropertyChanged>(target);

            target.PropertyChanged += HandleTargetPropertyChanged;
            if (target is INotifyCollectionChanged notifyCollectionChanged)
            {
                notifyCollectionChanged.CollectionChanged += HandleTargetCollectionChanged;
            }

            foreach (var propertyInfo in observer.GetPropertyInfos(target.GetType()))
            {
                object? value;
                try
                {
                    value = propertyInfo.GetValue(target);
                }
                catch
                {
                    value = null;
                }

                ObserveObject(propertyInfo.Name, value);
            }

            switch (target)
            {
                case IList list:
                {
                    _listItemCount = list.Count;
                    for (var i = 0; i < list.Count; i++)
                    {
                        ObserveObject(i.ToString(), list[i]);
                    }
                    break;
                }
                case IDictionary dictionary:
                {
                    var enumerator = dictionary.GetEnumerator();
                    using var _ = enumerator as IDisposable;
                    while (enumerator.MoveNext())
                    {
                        var entry = enumerator.Entry;
                        if (entry.Key.ToString() is not { Length: > 0 } key) continue;
                        ObserveObject(key, entry.Value);
                    }

                    break;
                }
            }
        }

        ~Observation()
        {
            Dispose();
        }

        /// <summary>
        /// Handles the PropertyChanged event of the observed object.
        /// Evaluates whether the changed property is being observed and updates the observation if necessary.
        /// </summary>
        /// <param name="sender">The object that raised the event.</param>
        /// <param name="e">The event arguments containing the property name.</param>
        private void HandleTargetPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isDisposed) return;
            if (e.PropertyName is null) return;
            if (!_targetReference.TryGetTarget(out var target)) return;
            if (_observer.GetPropertyInfo(_targetType, e.PropertyName) is not { } propertyInfo) return;

            object? value;
            try
            {
                value = propertyInfo.GetValue(target);
            }
            catch
            {
                value = null;
            }

            _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath + e.PropertyName, value));
            ObserveObject(e.PropertyName, value);
        }

        /// <summary>
        /// Handles the CollectionChanged event for collections (IList) and dictionaries (IDictionary).
        /// Manages observations for items added, removed, replaced, or moved within the collection.
        /// </summary>
        /// <param name="sender">The collection that raised the event.</param>
        /// <param name="e">The event arguments describing the collection change.</param>
        private void HandleTargetCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (_isDisposed) return;

            if (sender is IDictionary dictionary)
            {
                HandleDictionaryCollectionChanged(dictionary, e);
                return;
            }

            if (sender is not IList list) return;

            // Determine the range of indices that need to be updated.
            // Using a range ensures we correctly handle index shifts for Add/Remove/Move operations.
            var startUpdateIndex = -1;
            var endUpdateIndex = -1;
            var notifyCollectionObject = false;

            switch (e.Action)
            {
                case NotifyCollectionChangedAction.Add:
                    // Items added. Indices from NewStartingIndex onwards are shifted.
                    // We must re-observe everything from the insertion point to the end.
                    startUpdateIndex = e.NewStartingIndex;
                    endUpdateIndex = list.Count; 
                    break;

                case NotifyCollectionChangedAction.Remove:
                    // Items removed. Indices from OldStartingIndex onwards are shifted down.
                    // We must re-observe from the removal point to the end.
                    startUpdateIndex = e.OldStartingIndex;
                    endUpdateIndex = list.Count;
                    
                    // Notify that the collection object itself has changed (common pattern for removals).
                    notifyCollectionObject = true; 
                    break;

                case NotifyCollectionChangedAction.Replace:
                    // Items replaced. Indices do not shift.
                    // Only the range of replaced items needs to be updated.
                    startUpdateIndex = e.NewStartingIndex;
                    endUpdateIndex = e.NewStartingIndex + (e.NewItems?.Count ?? 0);
                    break;

                case NotifyCollectionChangedAction.Move:
                    // Items moved. Indices between OldStartingIndex and NewStartingIndex are affected.
                    // We re-observe the range spanning both the old and new positions.
                    startUpdateIndex = Math.Min(e.OldStartingIndex, e.NewStartingIndex);
                    // The number of items moved is usually e.NewItems.Count (or OldItems.Count).
                    // We extend the range to cover the moved items at their destination.
                    endUpdateIndex = Math.Max(e.OldStartingIndex, e.NewStartingIndex) + (e.NewItems?.Count ?? 0); 
                    break;

                case NotifyCollectionChangedAction.Reset:
                    // Collection reset (cleared or drastically changed).
                    // We must re-observe the entire collection.
                    startUpdateIndex = 0;
                    endUpdateIndex = list.Count;
                    notifyCollectionObject = true;
                    break;
            }

            // Cleanup observations for indices that no longer exist (e.g., list shrank).
            // _listItemCount tracks the previous size of the list.
            if (_listItemCount > list.Count)
            {
                for (var i = list.Count; i < _listItemCount; i++)
                {
                    ObserveObject(i.ToString(), null);
                }
            }

            // Update observations for the affected range and notify listeners.
            if (startUpdateIndex != -1)
            {
                for (var i = startUpdateIndex; i < endUpdateIndex; i++)
                {
                    // Ensure we don't go out of bounds if logic above was loose.
                    if (i >= list.Count) break;

                    var val = list[i];
                    
                    // Re-observe: Updates the internal tracking and attaches handlers to the new item.
                    ObserveObject(i.ToString(), val);
                    
                    // Notify: The value at this index path has changed.
                    _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath + i, val));
                }
            }
            
            // If the operation implies a change to the collection object itself (like Remove or Reset),
            // notify the listener about the collection path.
            if (notifyCollectionObject)
            {
                _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath.TrimEnd(':'), sender));
            }

            // Update the cached item count for the next event.
            _listItemCount = list.Count;
        }

        /// <summary>
        /// Handles CollectionChanged events specifically for IDictionary targets.
        /// Dictionaries use keys as paths, so index shifting is not a concern.
        /// </summary>
        /// <param name="dictionary">The dictionary that changed.</param>
        /// <param name="e">The event arguments.</param>
        private void HandleDictionaryCollectionChanged(IDictionary dictionary, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                // On Reset, we need to reconcile the current observations with the new state of the dictionary.
                
                // 1. Identify properties that are part of the object structure (not dictionary entries) to preserve them.
                var properties = _observer.GetPropertyInfos(_targetType).Select(p => p.Name).ToHashSet();
                
                // 2. Remove observations for keys that are no longer in the dictionary (and aren't properties).
                // We iterate a copy of keys to safely modify the collection.
                foreach (var key in _observations.Keys.ToArray())
                {
                    if (!properties.Contains(key))
                    {
                        // Effectively un-observe by passing null.
                        ObserveObject(key, null);
                    }
                }

                // 3. Observe all current entries in the dictionary.
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (entry.Key.ToString() is { Length: > 0 } key)
                    {
                        ObserveObject(key, entry.Value);
                    }
                }

                // 4. Notify that the dictionary object itself has changed.
                _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath.TrimEnd(':'), dictionary));
                return;
            }

            // Handle Removed items
            if (e.OldItems is not null)
            {
                foreach (var item in e.OldItems)
                {
                    if (GetKeyFromItem(item) is { } key)
                    {
                        var keyString = key.ToString()!;
                        ObserveObject(keyString, null); // Stop observing
                        _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath.TrimEnd(':'), dictionary)); // Notify removal
                    }
                }
            }

            // Handle Added/New items
            if (e.NewItems is not null)
            {
                foreach (var item in e.NewItems)
                {
                    if (GetKeyFromItem(item) is { } key)
                    {
                        var value = GetValueFromItem(item);
                        var keyString = key.ToString()!;
                        ObserveObject(keyString, value); // Start observing
                        _observer._handler.Invoke(new ObjectObserverChangedEventArgs(_basePath + keyString, value)); // Notify addition
                    }
                }
            }
        }

        private static object? GetKeyFromItem(object item)
        {
            switch (item)
            {
                case IKeyValuePair kvp:
                    return kvp.Key;
                case DictionaryEntry de:
                    return de.Key;
            }

            var type = item.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return type.GetProperty("Key")?.GetValue(item);
            }

            return null;
        }

        private static object? GetValueFromItem(object item)
        {
            switch (item)
            {
                case IKeyValuePair kvp:
                    return kvp.Value;
                case DictionaryEntry de:
                    return de.Value;
            }

            var type = item.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                return type.GetProperty("Value")?.GetValue(item);
            }

            return null;
        }

        private void ObserveObject(string path, object? target)
        {
            if (target is not INotifyPropertyChanged notifyPropertyChanged)
            {
                _observations.TryRemove(path, out var observation);
                observation?.Dispose();
            }
            else
            {
                _observations.AddOrUpdate(
                    path,
                    _ => new Observation(_basePath + path, notifyPropertyChanged, _observer),
                    (_, o) =>
                    {
                        if (o._targetReference.TryGetTarget(out var t) && Equals(t, notifyPropertyChanged)) return o;

                        o.Dispose();
                        return new Observation(_basePath + path, notifyPropertyChanged, _observer);
                    });
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;

            _isDisposed = true;
            GC.SuppressFinalize(this);

            if (!_targetReference.TryGetTarget(out var target)) return;

            target.PropertyChanged -= HandleTargetPropertyChanged;
            if (target is INotifyCollectionChanged notifyCollectionChanged)
            {
                notifyCollectionChanged.CollectionChanged -= HandleTargetCollectionChanged;
            }
        }
    }
}
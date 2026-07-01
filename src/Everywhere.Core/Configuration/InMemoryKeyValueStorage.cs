using System.Collections.Concurrent;

namespace Everywhere.Configuration;

/// <summary>
/// A simple in-memory implementation of <see cref="IKeyValueStorage"/> for non-persistent scenarios.
/// </summary>
public sealed class InMemoryKeyValueStorage : IKeyValueStorage
{
    private readonly ConcurrentDictionary<string, object?> _store = new();

    public T? Get<T>(string key, T? defaultValue = default)
    {
        if (_store.TryGetValue(key, out var value) && value is T typedValue)
        {
            return typedValue;
        }
        return defaultValue;
    }

    public void Set<T>(string key, T? value)
    {
        if (value == null)
        {
            Remove(key);
            return;
        }
        _store[key] = value;
    }

    public bool Contains(string key) => _store.ContainsKey(key);

    public void Remove(string key) => _store.TryRemove(key, out _);
}
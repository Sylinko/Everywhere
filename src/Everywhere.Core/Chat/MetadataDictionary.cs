using System.Collections.Immutable;

namespace Everywhere.Chat;

/// <summary>
/// Represents a dictionary for metadata storage.
/// </summary>
public readonly record struct MetadataDictionary : IReadOnlyDictionary<string, object?>
{
    public static MetadataDictionary Empty => default;

    public int Count => Dictionary.Count;
    public object? this[string key] => Dictionary[key];
    public IEnumerable<string> Keys => Dictionary.Keys;
    public IEnumerable<object?> Values => Dictionary.Values;

    private ImmutableDictionary<string, object?> Dictionary => field ?? ImmutableDictionary<string, object?>.Empty;

    private MetadataDictionary(ImmutableDictionary<string, object?> dictionary)
    {
        Dictionary = dictionary.IsEmpty ? null : dictionary;
    }

    public static MetadataDictionary FromDictionary(IDictionary<string, object?> dictionary)
    {
        return dictionary.Count == 0 ? Empty : new MetadataDictionary(ImmutableDictionary.CreateRange(dictionary));
    }

    public ImmutableDictionary<string, object?>.Enumerator GetEnumerator()
    {
        return Dictionary.GetEnumerator();
    }

    public bool ContainsKey(string key)
    {
        return Dictionary.ContainsKey(key);
    }

    public bool TryGetValue(string key, out object? value)
    {
        return Dictionary.TryGetValue(key, out value);
    }

    public MetadataDictionary SetItem(string key, object? value)
    {
        return new MetadataDictionary(Dictionary.SetItem(key, value));
    }

    IEnumerator<KeyValuePair<string, object?>> IEnumerable<KeyValuePair<string, object?>>.GetEnumerator()
    {
        return Dictionary.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return Dictionary.GetEnumerator();
    }
}
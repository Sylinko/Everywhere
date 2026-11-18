using System.Collections;
using System.Collections.Immutable;

namespace Everywhere.SourceGeneration;

/// <summary>
/// Compares ordered generator model collections by content rather than backing-array identity.
/// </summary>
internal readonly struct EquatableArray<T>(ImmutableArray<T> items) : IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    /// <summary>
    /// Gets the number of model values.
    /// </summary>
    public int Length => _items.Length;

    /// <summary>
    /// Gets a model value at its output-order position.
    /// </summary>
    public T this[int index] => _items[index];

    private readonly ImmutableArray<T> _items = items;

    /// <inheritdoc />
    public bool Equals(EquatableArray<T> other) => _items.SequenceEqual(other._items);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 17;
        foreach (var item in _items)
        {
            hash = unchecked(hash * 31 + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item)));
        }
        return hash;
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

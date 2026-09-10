using System.Diagnostics.CodeAnalysis;

namespace Everywhere.Automation;

/// <summary>
/// Identifies one typed metadata value attached to a <see cref="VisualElement" />.
/// </summary>
/// <typeparam name="TValue">The non-null metadata value type.</typeparam>
/// <remarks>
/// Keys with the same <see cref="Name" /> and <typeparamref name="TValue" /> identify the same metadata value, including when independently constructed.
/// </remarks>
public readonly record struct VisualElementMetadataKey<TValue> where TValue : notnull
{
    /// <summary>
    /// Gets the name that identifies this key within its value type.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Creates a typed metadata key with the specified name.
    /// </summary>
    public VisualElementMetadataKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// Stores typed metadata accumulated for one active <see cref="VisualElement" /> incarnation.
/// </summary>
/// <remarks>
/// Metadata does not participate in element identity and is not carried into a later incarnation of the same platform identity.
/// Values are ordinary references: this collection neither retains visual elements nor owns or disposes native resources held by a value.
/// Access follows the owning <see cref="VisualContext" />'s caller-serialization boundary.
/// </remarks>
public sealed class VisualElementMetadata
{
    /// <summary>
    /// Gets the number of stored metadata values.
    /// </summary>
    public int Count => _values?.Count ?? 0;

    private Dictionary<string, object>? _values;

    /// <summary>
    /// Adds or replaces the value associated with <paramref name="key" />.
    /// </summary>
    public void Set<TValue>(VisualElementMetadataKey<TValue> key, TValue value) where TValue : notnull
    {
        ArgumentNullException.ThrowIfNull(value);
        (_values ??= [])[key.Name] = value;
    }

    /// <summary>
    /// Attempts to get the value associated with <paramref name="key" />.
    /// </summary>
    public bool TryGetValue<TValue>(VisualElementMetadataKey<TValue> key, [MaybeNullWhen(false)] out TValue value) where TValue : notnull
    {
        if (_values is not null && _values.TryGetValue(key.Name, out var storedValue))
        {
            value = (TValue)storedValue;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Removes the value associated with <paramref name="key" />, if present.
    /// </summary>
    public bool Remove<TValue>(VisualElementMetadataKey<TValue> key) where TValue : notnull
    {
        return _values?.Remove(key.Name) == true;
    }
}
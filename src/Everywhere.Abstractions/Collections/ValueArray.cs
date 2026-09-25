using System.Text.Json;
using System.Text.Json.Serialization;
using MessagePack;
using MessagePack.Formatters;
using ZLinq;

namespace Everywhere.Collections;

/// <summary>
/// A value type wrapper for an array that implements sequence equality.
/// </summary>
/// <param name="array"></param>
/// <typeparam name="T"></typeparam>
[CollectionBuilder(typeof(ValueArrayBuilder), "Create")]
[JsonConverter(typeof(ValueArrayJsonConverterFactory))]
[MessagePackFormatter(typeof(ValueArrayMessagePackFormatter<>))]
public readonly struct ValueArray<T>(T[]? array) : IReadOnlyList<T>, IEquatable<ValueArray<T>>, IStructuralEquatable
{
    public int Count => _array?.Length ?? 0;

    public T this[int index] => _array is null ? throw new IndexOutOfRangeException() : _array[index];

    private readonly T[]? _array = array;

    public IEnumerator<T> GetEnumerator()
    {
        return _array is null ? Enumerable.Empty<T>().GetEnumerator() : ((IEnumerable<T>)_array).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    public bool Equals(ValueArray<T> other)
    {
        // Missing JSON fields produce default structs; they have the same value as empty arrays.
        return (_array ?? []).SequenceEqual(other._array ?? []);
    }

    public bool Equals(object? other, IEqualityComparer comparer)
    {
        return other is ValueArray<T> otherArray && SequenceEqual(_array ?? [], otherArray._array ?? [], comparer);

        static bool SequenceEqual(T[] first, T[] second, IEqualityComparer comparer)
        {
            if (first.Length != second.Length) return false;
            return !first.AsValueEnumerable().Where((t, i) => !comparer.Equals(t, second[i])).Any();
        }
    }

    public int GetHashCode(IEqualityComparer comparer)
    {
        var hashCode = new HashCode();
        if (_array is not null)
        {
            foreach (var item in _array) hashCode.Add(item is null ? 0 : comparer.GetHashCode(item));
        }
        return hashCode.ToHashCode();
    }

    public override bool Equals(object? obj)
    {
        return obj is ValueArray<T> valueArray && Equals(valueArray);
    }

    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        if (_array is not null)
        {
            foreach (var item in _array) hashCode.Add(item?.GetHashCode() ?? 0);
        }
        return hashCode.ToHashCode();
    }

    public static bool operator ==(ValueArray<T> left, ValueArray<T> right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ValueArray<T> left, ValueArray<T> right)
    {
        return !(left == right);
    }
}

public static class ValueArrayBuilder
{
    public static ValueArray<T> Create<T>(ReadOnlySpan<T> items)
    {
        return new ValueArray<T>([.. items]);
    }
}

public sealed class ValueArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ValueArray<>);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var elementType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ValueArrayJsonConverter<>).MakeGenericType(elementType);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}

public sealed class ValueArrayJsonConverter<T> : JsonConverter<ValueArray<T>>
{
    public override ValueArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var array = JsonSerializer.Deserialize<T[]>(ref reader, options);
        return new ValueArray<T>(array);
    }

    public override void Write(Utf8JsonWriter writer, ValueArray<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Serializes <see cref="ValueArray{T}"/> with the same one-dimensional representation as its
/// backing array. The wrapper exists for value equality and must not alter the wire shape.
/// </summary>
public sealed class ValueArrayMessagePackFormatter<T> : IMessagePackFormatter<ValueArray<T>>
{
    public void Serialize(ref MessagePackWriter writer, ValueArray<T> value, MessagePackSerializerOptions options)
    {
        var formatter = options.Resolver.GetFormatterWithVerify<T>();
        writer.WriteArrayHeader(value.Count);

        foreach (var item in value)
        {
            writer.CancellationToken.ThrowIfCancellationRequested();
            formatter.Serialize(ref writer, item, options);
        }
    }

    public ValueArray<T> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return default;

        var length = reader.ReadArrayHeader();
        if (length == 0) return default;

        var formatter = options.Resolver.GetFormatterWithVerify<T>();
        var items = new T[length];
        options.Security.DepthStep(ref reader);
        try
        {
            for (var i = 0; i < length; i++)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
                items[i] = formatter.Deserialize(ref reader, options);
            }
        }
        finally
        {
            reader.Depth--;
        }

        return new ValueArray<T>(items);
    }
}
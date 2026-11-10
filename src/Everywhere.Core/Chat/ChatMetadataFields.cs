using System.Globalization;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Logical identity and capture policy of shared metadata, independent of its database layout.
/// Runtime properties and EF entities retain their normal strongly typed storage.
/// </summary>
public abstract class ChatMetadataField(string key, bool isIndependent)
{
    /// <summary>
    /// Stable synchronization key, independent of CLR property names and database columns.
    /// </summary>
    public string Key { get; } = key;

    /// <summary>
    /// Whether this field can publish while message content is still changing.
    /// </summary>
    public bool IsIndependent { get; } = isIndependent;
}

/// <summary>
/// Encodes one known value at the persistence/synchronization boundary, not in runtime getters.
/// </summary>
public sealed class ChatMetadataField<T>(
    string key,
    bool isIndependent,
    Func<T, byte[]> serialize,
    Func<byte[], T> deserialize) : ChatMetadataField(key, isIndependent)
{
    /// <summary>
    /// Encodes a typed stored value for a frozen upload.
    /// </summary>
    public byte[] Serialize(T value) => serialize(value);

    /// <summary>
    /// Decodes a received value understood by this client.
    /// </summary>
    public T Deserialize(byte[] value) => deserialize(value);
}

/// <summary>
/// Stable field definitions. The persistence mapper associates them with concrete entity members.
/// Unknown wire fields do not require definitions and remain opaque in the entity's extension data.
/// </summary>
public static class ChatMetadataFields
{
    /// <summary>
    /// User-visible title, independently editable during generation.
    /// </summary>
    public static ChatMetadataField<string?> Topic { get; } = Create<string?>("topic", true);

    /// <summary>
    /// User-controlled history pin, independent of content activity.
    /// </summary>
    public static ChatMetadataField<bool> IsPinned { get; } = Create<bool>("isPinned", true);

    /// <summary>
    /// Creation time, retaining the existing UTC round-trip string encoding.
    /// </summary>
    public static ChatMetadataField<DateTimeOffset> CreatedAt { get; } = new("createdAt", true, SerializeDate, DeserializeDate);

    /// <summary>
    /// Content activity time, which must not advance in the cloud ahead of its content.
    /// </summary>
    public static ChatMetadataField<DateTimeOffset> UpdatedAt { get; } = new("updatedAt", false, SerializeDate, DeserializeDate);

    /// <summary>
    /// Defines a field using the ordinary MessagePack encoding for its type.
    /// </summary>
    public static ChatMetadataField<T> Create<T>(string key, bool isIndependent)
    {
        return new ChatMetadataField<T>(
            key, isIndependent,
            value => MessagePackSerializer.Serialize(value),
            value => MessagePackSerializer.Deserialize<T>(value));
    }

    private static byte[] SerializeDate(DateTimeOffset value)
    {
        return MessagePackSerializer.Serialize(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
    }

    private static DateTimeOffset DeserializeDate(byte[] value)
    {
        return DateTimeOffset.Parse(MessagePackSerializer.Deserialize<string>(value), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
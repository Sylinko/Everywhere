using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;

namespace Everywhere.Storage;

/// <summary>
/// Maps strongly typed runtime metadata, detached EF rows and wire fields. Known values
/// live only in their columns; unrecognized wire values live only in ExtensionData.
/// </summary>
public static class ChatMetadataMapper
{
    private static readonly Mapping[] Mappings =
    [
        Map(
            ChatMetadataFields.Topic,
            metadata => metadata.Topic,
            (metadata, value) => metadata.Topic = value,
            row => row.Topic,
            (row, value) => row.Topic = value),
        Map(
            ChatMetadataFields.IsPinned,
            metadata => metadata.IsPinned,
            (metadata, value) => metadata.IsPinned = value,
            row => row.IsPinned,
            (row, value) => row.IsPinned = value),
        Map(
            ChatMetadataFields.CreatedAt,
            metadata => metadata.DateCreated,
            (metadata, value) => metadata.DateCreated = value,
            row => row.CreatedAt,
            (row, value) => row.CreatedAt = value),
        Map(
            ChatMetadataFields.UpdatedAt,
            metadata => metadata.DateModified,
            (metadata, value) => metadata.DateModified = value,
            row => row.UpdatedAt,
            (row, value) => row.UpdatedAt = value)
    ];

    /// <summary>
    /// Field keys eligible for capture without waiting for stable message content.
    /// </summary>
    public static IReadOnlyList<string> IndependentKeys { get; } =
        Mappings
            .AsValueEnumerable()
            .Where(mapping => mapping.Field.IsIndependent)
            .Select(mapping => mapping.Field.Key)
            .ToArray();

    /// <summary>
    /// Captures a detached persistence DTO without retaining or serializing the live model.
    /// Archive and account state are committed by their own operations, not ordinary saves.
    /// </summary>
    public static ChatV2Entity Capture(ChatMetadata metadata)
    {
        var row = new ChatV2Entity { Id = metadata.Id, IsTemporary = metadata.IsTemporary };
        foreach (var mapping in Mappings)
        {
            mapping.CopyFromModel(metadata, row);
        }
        return row;
    }

    /// <summary>
    /// Materializes ordinary metadata without loading nodes or decoding unknown extensions.
    /// </summary>
    public static ChatMetadata Materialize(ChatV2Entity row)
    {
        var metadata = new ChatMetadata(row.Id, row.CreatedAt, row.UpdatedAt, row.Topic);
        Publish(row, metadata);
        return metadata;
    }

    /// <summary>
    /// Publishes committed values into an existing runtime identity without recording local edits.
    /// </summary>
    public static void Publish(ChatV2Entity row, ChatMetadata metadata)
    {
        metadata.IsPersisted = true;
        foreach (var mapping in Mappings)
        {
            mapping.CopyToModel(row, metadata);
        }
        metadata.ArchivedAt = row.ArchivedAt;
        metadata.IsTemporary = row.IsTemporary;
    }

    /// <summary>
    /// Copies only captured local fields; untouched metadata and opaque extensions remain stored.
    /// </summary>
    public static void CopyFields(ChatV2Entity snapshot, ChatV2Entity destination, IEnumerable<ChatMetadataField> fields)
    {
        foreach (var field in fields)
        {
            Mappings.Single(mapping => mapping.Field == field).Copy(snapshot, destination);
        }
    }

    /// <summary>
    /// Encodes known columns and retains unknown bytes for synchronization capture and merge.
    /// The dictionary is a boundary snapshot, not runtime backing storage or another database table.
    /// </summary>
    public static Dictionary<string, byte[]> ReadFields(ChatV2Entity row)
    {
        var fields = ReadExtensions(row.ExtensionData);
        foreach (var mapping in Mappings)
        {
            fields[mapping.Field.Key] = mapping.Serialize(row);
        }
        return fields;
    }

    /// <summary>
    /// Installs a complete merged wire snapshot into the appropriate columns or extension container.
    /// </summary>
    public static void ApplyFields(ChatV2Entity row, IReadOnlyDictionary<string, byte[]> fields)
    {
        var extensions = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, value) in fields)
        {
            var mapping = Mappings.FirstOrDefault(mapping => mapping.Field.Key == key);
            if (mapping is not null)
            {
                mapping.Deserialize(row, value);
            }
            else
            {
                extensions.Add(key, value);
            }
        }
        row.ExtensionData = extensions.Count == 0 ? null : MessagePackSerializer.Serialize(extensions);
    }

    private static Dictionary<string, byte[]> ReadExtensions(byte[]? value)
    {
        return value is null ?
            new Dictionary<string, byte[]>(StringComparer.Ordinal) :
            MessagePackSerializer.Deserialize<Dictionary<string, byte[]>>(value);
    }

    private static Mapping Map<T>(
        ChatMetadataField<T> field,
        Func<ChatMetadata, T> readModel,
        Action<ChatMetadata, T> writeModel,
        Func<ChatV2Entity, T> readRow,
        Action<ChatV2Entity, T> writeRow)
    {
        return new Mapping(
            field,
            (metadata, row) => writeRow(row, readModel(metadata)),
            (row, metadata) => writeModel(metadata, readRow(row)),
            (source, destination) => writeRow(destination, readRow(source)),
            row => field.Serialize(readRow(row)),
            (row, value) => writeRow(row, field.Deserialize(value)));
    }

    private sealed record Mapping(
        ChatMetadataField Field,
        Action<ChatMetadata, ChatV2Entity> CopyFromModel,
        Action<ChatV2Entity, ChatMetadata> CopyToModel,
        Action<ChatV2Entity, ChatV2Entity> Copy,
        Func<ChatV2Entity, byte[]> Serialize,
        Action<ChatV2Entity, byte[]> Deserialize
    );
}
using Everywhere.Cloud;

namespace Everywhere.Database;

// SQLite keeps the original text keys, including lifecycle actions in the shared Key column.
internal static class ChatSyncStorageKeys
{
    /// <summary>
    /// Maps a known record kind to the canonical SQLite discriminator.
    /// </summary>
    public static string ToStorageKey(this ChatSyncRecordKind value)
    {
        return value switch
        {
            ChatSyncRecordKind.Node => "node",
            ChatSyncRecordKind.Choice => "choice",
            ChatSyncRecordKind.Field => "field",
            ChatSyncRecordKind.Lifecycle => "lifecycle",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    /// <summary>
    /// Reads a SQLite discriminator; unknown stored values are rejected rather than misinterpreted.
    /// </summary>
    public static ChatSyncRecordKind ReadRecordKind(string key)
    {
        return key switch
        {
            "node" => ChatSyncRecordKind.Node,
            "choice" => ChatSyncRecordKind.Choice,
            "field" => ChatSyncRecordKind.Field,
            "lifecycle" => ChatSyncRecordKind.Lifecycle,
            _ => throw new InvalidDataException($"Unknown chat sync RecordKind key: {key}."),
        };
    }

    /// <summary>
    /// Maps a lifecycle intent to its persisted action key.
    /// </summary>
    public static string ToStorageKey(this ChatSyncAction value)
    {
        return value switch
        {
            ChatSyncAction.Update => "update",
            ChatSyncAction.Archive => "archive",
            ChatSyncAction.Restore => "restore",
            ChatSyncAction.Delete => "delete",
            ChatSyncAction.Clear => "clear",
            ChatSyncAction.Republish => "republish",
            _ => throw new ArgumentOutOfRangeException(nameof(value)),
        };
    }

    /// <summary>
    /// Reads a persisted lifecycle intent; unrecognized actions are rejected.
    /// </summary>
    public static ChatSyncAction ReadAction(string key)
    {
        return key switch
        {
            "update" => ChatSyncAction.Update,
            "archive" => ChatSyncAction.Archive,
            "restore" => ChatSyncAction.Restore,
            "delete" => ChatSyncAction.Delete,
            "clear" => ChatSyncAction.Clear,
            "republish" => ChatSyncAction.Republish,
            _ => throw new InvalidDataException($"Unknown chat sync Action key: {key}."),
        };
    }
}
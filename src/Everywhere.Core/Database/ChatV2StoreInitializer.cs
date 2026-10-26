using System.Buffers.Binary;
using Everywhere.Chat;
using Everywhere.Common;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Database;

/// <summary>
/// Initializes the separate local store. The legacy source is read-only, including its WAL;
/// imported rows and the initialization marker commit in one destination transaction.
/// </summary>
public sealed class ChatV2StoreInitializer(
    IDbContextFactory<ChatV2DbContext> dbFactory,
    string? legacyDatabasePath,
    ILogger<ChatV2StoreInitializer> logger
) : IAsyncInitializer
{
    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var destination = await dbFactory.CreateDbContextAsync(cancellationToken);
        await destination.Database.OpenConnectionAsync(cancellationToken);
        await destination.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;", cancellationToken);
        await destination.Database.MigrateAsync(cancellationToken);

        await using var transaction = await destination.Database.BeginTransactionAsync(cancellationToken);
        if (await destination.StoreInfo.AnyAsync(cancellationToken))
        {
            return;
        }

        var hasLegacyDatabase = legacyDatabasePath is not null && File.Exists(legacyDatabasePath);
        if (hasLegacyDatabase)
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = legacyDatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString();
            await using var source = new SqliteConnection(connectionString);
            await source.OpenAsync(cancellationToken);
            await using var sourceTransaction = source.BeginTransaction(deferred: true);
            await ImportChatsAsync(source, sourceTransaction, destination, cancellationToken);
            await ImportBlobsAsync(source, sourceTransaction, destination, cancellationToken);
        }

        var initializedAt = DateTimeOffset.UtcNow;
        var importedAtTicks = hasLegacyDatabase ? initializedAt.UtcTicks : (long?)null;
        await destination.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO StoreInfo (Id, InitializedAt, LegacyImportedAt) VALUES (1, {initializedAt.UtcTicks}, {importedAtTicks})",
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task ImportChatsAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        ChatV2DbContext destination,
        CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.Transaction = sourceTransaction;
        command.CommandText = "SELECT Id, Topic, CreatedAt, UpdatedAt, IsDeleted FROM Chats";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var chatId = reader.GetGuid(0);
            var topic = reader.IsDBNull(1) ? null : reader.GetString(1);
            var createdAt = reader.GetInt64(2);
            var updatedAt = reader.GetInt64(3);
            var isArchived = reader.GetBoolean(4);
            var archivedAt = isArchived ? updatedAt : (long?)null;
            var prepared = default(PreparedNodes);
            try
            {
                // Finish conversion before writing any rows for this chat. A bad payload must
                // not leave partial history behind or require a per-chat transaction/savepoint.
                prepared = await PrepareNodesAsync(source, sourceTransaction, chatId, isArchived, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or MessagePackSerializationException or EndOfStreamException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                logger.LogWarning(exception, "Skipping legacy chat {ChatId} because its history could not be converted.", chatId);
                continue;
            }

            await destination.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Chats (Id, Topic, CreatedAt, UpdatedAt, ArchivedAt) VALUES ({chatId}, {topic}, {createdAt}, {updatedAt}, {archivedAt})",
                cancellationToken);
            await WriteNodesAsync(destination, chatId, prepared, cancellationToken);
        }
    }

    private static async Task<PreparedNodes> PrepareNodesAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        Guid chatId,
        bool isArchived,
        CancellationToken cancellationToken)
    {
        var nodes = new Dictionary<Guid, ChatV2NodeEntity>();
        await using (var command = source.CreateCommand())
        {
            command.Transaction = sourceTransaction;
            command.CommandText = """
                SELECT Id, ParentId, ChoiceChildId, Payload, CreatedAt, UpdatedAt
                FROM Nodes WHERE ChatContextId = $chatId AND (IsDeleted = 0 OR $isArchived = 1)
                """;
            command.Parameters.AddWithValue("$chatId", chatId);
            command.Parameters.AddWithValue("$isArchived", isArchived);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var payload = (byte[])reader.GetValue(3);
                var kind = ReadMessageKind(payload);
                var row = new ChatV2NodeEntity
                {
                    ChatContextId = chatId,
                    Id = reader.GetGuid(0),
                    ParentId = reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    ChoiceChildId = reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    MessageKind = kind,
                    Payload = payload,
                    CreatedAt = new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                    UpdatedAt = new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                };
                nodes.Add(row.Id, row);
            }
        }

        if (nodes.Count == 0)
        {
            // Legacy empty conversations can have metadata without a persisted root.
            var rootId = Guid.Empty;
            nodes.Add(rootId, new ChatV2NodeEntity
            {
                ChatContextId = chatId,
                Id = rootId,
                MessageKind = ChatMessageKind.Root,
                Payload = MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()),
            });
        }

        var roots = nodes.Values.Where(node => node.ParentId is null).ToArray();
        if (roots.Length != 1 || roots[0].MessageKind != ChatMessageKind.Root)
        {
            throw new InvalidDataException($"Chat {chatId} does not have exactly one topology root.");
        }

        var root = roots[0];
        var children = nodes.Values.Where(node => node.ParentId.HasValue).ToLookup(node => node.ParentId.GetValueOrDefault());
        var pending = new Stack<ChatV2NodeEntity>();
        var visited = new HashSet<Guid>();
        pending.Push(root);
        while (pending.TryPop(out var parent))
        {
            if (!visited.Add(parent.Id))
            {
                throw new InvalidDataException($"Chat {chatId} contains a cycle in its retained topology.");
            }

            var siblings = children[parent.Id].OrderBy(node => GetLegacyOrderKey(node.Id)).ThenBy(node => node.Id).ToArray();
            for (var index = 0; index < siblings.Length; index++)
            {
                var child = siblings[index];
                child.SiblingOrder = index;
                child.TurnId = child.MessageKind.IsTurnStart() ? child.Id : parent.TurnId;
                pending.Push(child);
            }

            // Legacy null choices meant the first child because ChoiceIndex defaulted to zero.
            // A stored, invalid choice was clamped to -1 and ended the branch instead.
            parent.ChoiceChildId = parent.ChoiceChildId is { } chosen ?
                siblings.FirstOrDefault(child => child.Id == chosen)?.Id :
                siblings.FirstOrDefault()?.Id;

            if (parent.MessageKind == ChatMessageKind.Compression)
            {
                var message = (ContextCompressionChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(parent.Payload);
                if (message.HasSummary)
                {
                    parent.SummaryThroughNodeId = message.CoveredThroughNodeId == Guid.Empty ? root.Id : message.CoveredThroughNodeId;
                }
            }
        }

        if (visited.Count != nodes.Count)
        {
            throw new InvalidDataException($"Chat {chatId} has retained nodes disconnected from its root.");
        }

        var blobReferences = new HashSet<(Guid NodeId, string Sha256)>();
        foreach (var row in nodes.Values)
        {
            CollectPayloadReferences(row, blobReferences, cancellationToken);
        }

        await using var referencesCommand = source.CreateCommand();
        referencesCommand.Transaction = sourceTransaction;
        referencesCommand.CommandText = "SELECT DISTINCT ChatNodeId, BlobSha256 FROM NodeBlobs WHERE ChatContextId = $chatId";
        referencesCommand.Parameters.AddWithValue("$chatId", chatId);
        await using var references = await referencesCommand.ExecuteReaderAsync(cancellationToken);
        while (await references.ReadAsync(cancellationToken))
        {
            var nodeId = references.GetGuid(0);
            if (!nodes.ContainsKey(nodeId))
            {
                continue;
            }

            var sha256 = references.GetString(1);
            blobReferences.Add((nodeId, sha256));
        }

        return new PreparedNodes(nodes.Values, blobReferences);
    }

    private static void CollectPayloadReferences(
        ChatV2NodeEntity row,
        HashSet<(Guid NodeId, string Sha256)> references,
        CancellationToken cancellationToken)
    {
        if (row.MessageKind is not (ChatMessageKind.User or ChatMessageKind.UserStrategy or
            ChatMessageKind.Assistant or ChatMessageKind.FunctionCall))
        {
            return;
        }

        // The old node projection did not enumerate attachments in embedded child chats.
        // Decode retained payloads once during migration, without constructing a presentation,
        // and keep their original bytes. Preserve legacy references as well as recovered ones.
        var message = MessagePackSerializer.Deserialize<ChatMessage>(row.Payload, cancellationToken: cancellationToken);
        try
        {
            foreach (var file in ChatMessageResources.GetFileAttachments(message))
            {
                references.Add((row.Id, file.Sha256));
            }
        }
        finally
        {
            if (message is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    private static async Task WriteNodesAsync(
        ChatV2DbContext destination,
        Guid chatId,
        PreparedNodes prepared,
        CancellationToken cancellationToken)
    {
        foreach (var row in prepared.Nodes)
        {
            await destination.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO Nodes (ChatContextId, Id, ParentId, TurnId, SiblingOrder, ChoiceChildId,
                    MessageKind, SummaryThroughNodeId, Payload, CreatedAt, UpdatedAt)
                VALUES ({row.ChatContextId}, {row.Id}, {row.ParentId}, {row.TurnId}, {row.SiblingOrder}, {row.ChoiceChildId},
                    {(int)row.MessageKind}, {row.SummaryThroughNodeId}, {row.Payload}, {row.CreatedAt.UtcTicks}, {row.UpdatedAt.UtcTicks})
                """,
                cancellationToken);
        }

        foreach (var (nodeId, sha256) in prepared.References)
        {
            await destination.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO NodeBlobs (ChatContextId, ChatNodeId, BlobSha256) VALUES ({chatId}, {nodeId}, {sha256})",
                cancellationToken);
        }
    }

    private static async Task ImportBlobsAsync(
        SqliteConnection source,
        SqliteTransaction sourceTransaction,
        ChatV2DbContext destination,
        CancellationToken cancellationToken)
    {
        var hasLocalPath = false;
        await using (var schemaCommand = source.CreateCommand())
        {
            schemaCommand.Transaction = sourceTransaction;
            schemaCommand.CommandText = "PRAGMA table_info(Blobs)";
            await using var schemaReader = await schemaCommand.ExecuteReaderAsync(cancellationToken);
            while (await schemaReader.ReadAsync(cancellationToken))
            {
                hasLocalPath |= schemaReader.GetString(1) == "LocalPath";
            }
        }

        await using var command = source.CreateCommand();
        command.Transaction = sourceTransaction;
        command.CommandText = hasLocalPath ?
            "SELECT Sha256, LocalPath, MimeType, Size, CreatedAt, LastAccessAt FROM Blobs" :
            "SELECT Sha256, '', MimeType, Size, CreatedAt, LastAccessAt FROM Blobs";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var sha256 = reader.GetString(0);
            var localPath = reader.GetString(1);
            var mimeType = reader.GetString(2);
            var size = reader.GetInt64(3);
            var createdAt = reader.GetInt64(4);
            var lastAccessAt = reader.GetInt64(5);
            await destination.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Blobs (Sha256, LocalPath, MimeType, Size, CreatedAt, LastAccessAt) VALUES ({sha256}, {localPath}, {mimeType}, {size}, {createdAt}, {lastAccessAt})",
                cancellationToken);
        }
    }

    private static ChatMessageKind ReadMessageKind(byte[] payload)
    {
        var reader = new MessagePackReader(payload);
        if (reader.ReadArrayHeader() != 2)
        {
            throw new InvalidDataException("The legacy chat payload is not a message union.");
        }

        var kind = (ChatMessageKind)reader.ReadInt32();
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidDataException($"Unsupported legacy chat message kind: {kind}.");
        }

        return kind;
    }

    private static long GetLegacyOrderKey(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        return BinaryPrimitives.ReadInt64BigEndian(bytes);
    }

    private sealed record PreparedNodes(
        IReadOnlyCollection<ChatV2NodeEntity> Nodes,
        IReadOnlyCollection<(Guid NodeId, string Sha256)> References);
}
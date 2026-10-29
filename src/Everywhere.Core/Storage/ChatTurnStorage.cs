using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace Everywhere.Storage;

/// <summary>
/// Reads the v2 topology eagerly and materializes payloads only at explicit turn boundaries.
/// Runtime identity and loading coordination belong to the context manager.
/// </summary>
public sealed partial class ChatTurnStorage(IDbContextFactory<ChatV2DbContext> dbFactory) : IChatContextStorage
{
    /// <summary>
    /// Opens a conversation, including archived conversations, without reading its historical payloads.
    /// </summary>
    public async Task<ChatData> GetChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default)
    {
        var chatId = metadata.Id;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var readScope = await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var topology = await db.Nodes.AsNoTracking().Where(row => row.ChatContextId == chatId)
            .Select(row => new NodeTopology(
                row.Id,
                row.ParentId,
                row.TurnId,
                row.ChoiceChildId,
                row.MessageKind,
                row.SummaryThroughNodeId,
                row.CreatedAt,
                row.UpdatedAt))
            .ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var nodes = topology.AsValueEnumerable().ToDictionary(
            row => row.Id,
            row => new ChatMessageNode(row.Id, row.MessageKind, row.CreatedAt, row.UpdatedAt)
            {
                SummaryThroughNodeId = row.SummaryThroughNodeId,
            });
        var rootRow = topology.AsValueEnumerable().Single(row => row.ParentId is null);
        var root = nodes[rootRow.Id];
        root.TryLoadMessage(new RootChatMessage());

        foreach (var row in topology)
        {
            var node = nodes[row.Id];
            node.SetChoiceChild(row.ChoiceChildId);
            if (row.ParentId is { } parentId)
            {
                var parent = nodes[parentId];
                parent.AddChild(node.Id);
                node.Parent = parent;
            }
        }

        nodes.Remove(root.Id);
        var data = new ChatData(metadata, nodes.Values, root);
        foreach (var row in topology)
        {
            var node = data.FindNode(row.Id) ?? throw new InvalidDataException("A topology node is missing from its data.");
            if (node.Turn?.Id != row.TurnId)
            {
                data.Dispose();
                throw new InvalidDataException("Persisted turn membership does not match the parent path.");
            }
        }

        return data;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, ChatMessage>> LoadMessagesAsync(Guid chatId, Guid? turnId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        await using var transaction = ((SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await using var readScope = await db.Database.UseTransactionAsync(transaction, cancellationToken);
        var rows = await db.Nodes.AsNoTracking()
            .Where(row => row.ChatContextId == chatId && row.TurnId == turnId)
            .Select(row => new { row.Id, row.Payload })
            .ToArrayAsync(cancellationToken);
        var nodeIds = rows.Select(row => row.Id).ToArray();
        var blobs = await (
            from reference in db.NodeBlobs
            join blob in db.Blobs on reference.BlobSha256 equals blob.Sha256
            where reference.ChatContextId == chatId && ((IEnumerable<Guid>)nodeIds).Contains(reference.ChatNodeId)
            select blob).AsNoTracking().Distinct().ToDictionaryAsync(blob => blob.Sha256, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var messages = new Dictionary<Guid, ChatMessage>(rows.Length);
        try
        {
            foreach (var row in rows)
            {
                var message = MessagePackSerializer.Deserialize<ChatMessage>(row.Payload, cancellationToken: cancellationToken);
                messages.Add(row.Id, message);
                foreach (var file in ChatMessageResources.GetFileAttachments(message))
                {
                    if (blobs.TryGetValue(file.Sha256, out var blob) && !string.IsNullOrEmpty(blob.LocalPath))
                    {
                        file.FilePath = blob.LocalPath;
                    }
                }
            }

            return messages;
        }
        catch
        {
            foreach (var message in messages.Values.OfType<IDisposable>())
            {
                message.Dispose();
            }

            throw;
        }
    }

    /// <summary>
    /// Reads retained user text without materializing covered assistant/tool turns or attachment files.
    /// The owning context handles already loaded, unsaved text before requesting stored content.
    /// </summary>
    public async Task<string> ReadUserTextAsync(Guid chatId, Guid nodeId, ChatMessageKind kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (ChatMessageKind.User or ChatMessageKind.UserStrategy))
        {
            throw new ArgumentException("Only ordinary or strategy user messages supply retained user text.", nameof(kind));
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var payload = await db.Nodes.AsNoTracking()
            .Where(row => row.ChatContextId == chatId && row.Id == nodeId)
            .Select(row => row.Payload)
            .SingleAsync(cancellationToken);

        var reader = new MessagePackReader(payload);
        if (reader.ReadArrayHeader() != 2 || (ChatMessageKind)reader.ReadInt32() != kind || reader.ReadArrayHeader() < 1)
        {
            throw new InvalidDataException("The retained user payload does not match its topology.");
        }

        // Ordinary and strategy user messages share Content at key 0. Covered attachments and
        // strategy configuration are irrelevant to this text-only read and stay unmaterialized.
        return reader.ReadString() ?? string.Empty;
    }

    private sealed record NodeTopology(
        Guid Id,
        Guid? ParentId,
        Guid? TurnId,
        Guid? ChoiceChildId,
        ChatMessageKind MessageKind,
        Guid? SummaryThroughNodeId,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt
    );
}
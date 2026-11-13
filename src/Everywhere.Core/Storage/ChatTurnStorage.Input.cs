using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Storage;

partial class ChatTurnStorage
{
    /// <inheritdoc />
    public async Task<ChatInputSnapshot?> ReadInputAsync(Guid chatId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.ChatInputs.AsNoTracking().SingleOrDefaultAsync(row => row.SlotId == chatId, cancellationToken);
        return input is null ? null : MessagePackSerializer.Deserialize<ChatInputSnapshot>(input.Payload, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task<StoredChatInput?> ReadNewChatInputAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var input = await db.ChatInputs.AsNoTracking().SingleOrDefaultAsync(row => row.SlotId == Guid.Empty, cancellationToken);
        return input is null ?
            null :
            new StoredChatInput(
                input.ChatId,
                MessagePackSerializer.Deserialize<ChatInputSnapshot>(input.Payload, cancellationToken: cancellationToken));
    }

    /// <inheritdoc />
    public async Task<bool> SaveInputAsync(Guid chatId, ChatInputWrite input, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var isSaved = await WriteInputAsync(db, chatId, input, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return isSaved;
    }

    private static async Task<bool> WriteInputAsync(ChatV2DbContext db, Guid chatId, ChatInputWrite input, CancellationToken cancellationToken)
    {
        if (input.Payload is null)
        {
            // Owner-scoped deletion must not clear another context's new-chat slot.
            await db.ChatInputs.Where(row => row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
            return true;
        }

        var isPersisted = await db.Chats.AnyAsync(chat => chat.Id == chatId, cancellationToken);
        if (!isPersisted && !input.IsNewChatDraft)
        {
            // Standalone selection work never borrows the new-chat slot. Its first graph save
            // commits this input; until then the manager retains the captured write for retry.
            return false;
        }

        var slotId = isPersisted ? chatId : Guid.Empty;
        var now = DateTimeOffset.UtcNow;
        foreach (var file in input.Files)
        {
            await WriteBlobAsync(db, file, now, cancellationToken);
        }

        await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO ChatInputs (SlotId, ChatId, Payload) VALUES ({slotId}, {chatId}, {input.Payload})
             ON CONFLICT (SlotId) DO UPDATE SET ChatId = excluded.ChatId, Payload = excluded.Payload
             """,
            cancellationToken);

        if (isPersisted)
        {
            await db.ChatInputs.Where(row => row.SlotId == Guid.Empty && row.ChatId == chatId).ExecuteDeleteAsync(cancellationToken);
        }

        return true;
    }
}

/// <summary>
/// The single unsent chat's stable runtime identity and recoverable local input.
/// </summary>
public sealed record StoredChatInput(Guid ChatId, ChatInputSnapshot Snapshot);
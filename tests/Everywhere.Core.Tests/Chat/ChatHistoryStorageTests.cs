using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.Storage;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatHistoryStorageTests
{
    [Test]
    public async Task ReadHistoryPage_WhenAnchorChangesOrDisappears_ContinuesFromCapturedValues()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var db = files.Factory.CreateDbContext();
        var date = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(1, 5).Select(index => new ChatV2Entity
        {
            Id = Guid.Parse($"00000000-0000-0000-0000-{index:D12}"),
            CreatedAt = date,
            UpdatedAt = date,
            IsPinned = index <= 2
        }).ToArray();
        db.Chats.AddRange(rows);
        await db.SaveChangesAsync();

        var first = await storage.ReadHistoryPageAsync(1);
        Assert.That(first.Items.Single().Id, Is.EqualTo(rows[1].Id));
        await storage.SetPinnedAsync(rows[1].Id, false);
        await db.Chats.Where(row => row.Id == rows[1].Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.UpdatedAt, date.AddDays(1)));
        var second = await storage.ReadHistoryPageAsync(1, first.NextCursor);
        Assert.That(second.Items.Single().Id, Is.EqualTo(rows[0].Id));
        db.Chats.Remove(rows[0]);
        await db.SaveChangesAsync();

        var remainder = await storage.ReadHistoryPageAsync(10, second.NextCursor);
        Assert.Multiple(() =>
        {
            Assert.That(
                remainder.Items.Select(item => item.Id),
                Is.EqualTo(new[] { rows[1].Id, rows[4].Id, rows[3].Id, rows[2].Id }));
            Assert.That(remainder.NextCursor, Is.Null);
        });
    }

    [Test]
    public async Task Save_WhenOnlyPayloadChanges_PreservesChoicesAndUneditedMetadata()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        using var old = new ChatData();
        var chatId = old.Metadata.Id;
        var user = new UserChatMessage("old", []);
        var userNode = old.Add(user);
        var first = old.Add(new AssistantChatMessage());
        var alternative = old.Append(userNode, new AssistantChatMessage(), shouldSelectChild: false);
        old.Metadata.DateModified = DateTimeOffset.UtcNow.AddDays(-30);
        await storage.SaveDataAsync(old);
        using var recent = new ChatData();
        recent.Add(new UserChatMessage("recent", []));
        await storage.SaveDataAsync(recent);
        var oldDate = old.Metadata.DateModified;
        old.SelectChild(userNode, alternative);
        var (choiceBatch, choices, metadataChanges) = ChatWriteBatch.Capture(old);
        user.Content = "updated payload";
        old.Metadata.DateModified = oldDate;
        // A payload edit arriving during a choice save must not keep that committed choice dirty.
        await storage.SaveChatContextAsync(choiceBatch);
        old.PersistenceDirtyState.Acknowledge(choices);
        old.Metadata.Acknowledge(metadataChanges);
        var (captured, payloadChanges, payloadMetadataChanges) = ChatWriteBatch.Capture(old);
        var extensionData = MessagePackSerializer.Serialize(new Dictionary<string, byte[]> { ["newerField"] = [0x01] });
        await using (var db = files.Factory.CreateDbContext())
        {
            // Model a committed receive without asking a local editing API to impersonate it.
            await db.Chats.Where(row => row.Id == chatId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Topic, "received title")
                    .SetProperty(row => row.ExtensionData, extensionData));
            await db.Nodes.Where(row => row.ChatContextId == chatId && row.Id == userNode.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ChoiceChildId, first.Id));
        }

        await storage.SetPinnedAsync(chatId, true);
        await storage.SaveChatContextAsync(captured);
        old.PersistenceDirtyState.Acknowledge(payloadChanges);
        old.Metadata.Acknowledge(payloadMetadataChanges);
        await using (var db = files.Factory.CreateDbContext())
        {
            var saved = await db.Chats.SingleAsync(row => row.Id == chatId);
            Assert.That(saved.Topic, Is.EqualTo("received title"));
            Assert.That(saved.ExtensionData, Is.EqualTo(extensionData));
            Assert.That(saved.IsPinned, Is.True);
            Assert.That((await db.Nodes.SingleAsync(row => row.Id == userNode.Id)).ChoiceChildId, Is.EqualTo(first.Id));
            Assert.That(userNode.ChoiceChildId, Is.EqualTo(alternative.Id));
        }
        old.Metadata.Rename("renamed");
        await storage.SaveChatContextMetadataAsync(ChatMetadataWrite.Capture(old.Metadata, out _));
        var page = await storage.ReadHistoryPageAsync(1);
        Assert.That(page.Items.Single().Id, Is.EqualTo(chatId));
        Assert.That(page.Items.Single().DateModified, Is.EqualTo(oldDate));
        Assert.That((await storage.GetMostRecentChatMetadataAsync())?.Id, Is.EqualTo(recent.Metadata.Id));
    }
}
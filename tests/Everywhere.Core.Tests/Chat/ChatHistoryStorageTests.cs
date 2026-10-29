using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.Storage;
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
        rows[1].IsPinned = false;
        rows[1].UpdatedAt = date.AddDays(1);
        await db.SaveChangesAsync();
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
    public async Task SetPinned_WhenOtherMetadataAndNodesAreSaved_PreservesPinAndArchiveIndependently()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        using var old = new ChatData();
        old.Add(new UserChatMessage("old", []));
        old.Metadata.DateModified = DateTimeOffset.UtcNow.AddDays(-30);
        await storage.SaveDataAsync(old);
        using var recent = new ChatData();
        recent.Add(new UserChatMessage("recent", []));
        await storage.SaveDataAsync(recent);
        var oldDate = old.Metadata.DateModified;
        var (captured, _) = ChatWriteBatch.Capture(old);

        await storage.SetPinnedAsync(old.Metadata.Id, true);
        await storage.SaveChatContextAsync(captured);
        await storage.SaveChatContextMetadataAsync(new ChatMetadataWrite(old.Metadata.Id, "renamed", old.Metadata.DateCreated, oldDate, old.Metadata.IsTemporary));
        var page = await storage.ReadHistoryPageAsync(1);
        Assert.That(page.Items.Single().Id, Is.EqualTo(old.Metadata.Id));
        Assert.That(page.Items.Single().DateModified, Is.EqualTo(oldDate));
        Assert.That((await storage.GetMostRecentChatMetadataAsync())?.Id, Is.EqualTo(recent.Metadata.Id));

        await storage.ArchiveAsync(old.Metadata.Id);
        Assert.That((await storage.ReadHistoryPageAsync(10)).Items.Select(item => item.Id), Is.EqualTo(new[] { recent.Metadata.Id }));
        Assert.That((await storage.ReadHistoryPageAsync(10, view: ChatHistoryView.Archived)).Items.Single().IsPinned, Is.True);
        await storage.RestoreAsync(old.Metadata.Id);
        Assert.That((await storage.ReadHistoryPageAsync(1)).Items.Single().IsPinned, Is.True);
        await storage.SetPinnedAsync(old.Metadata.Id, false);
        Assert.That(
            (await storage.ReadHistoryPageAsync(10)).Items.Select(item => item.Id),
            Is.EqualTo(new[] { recent.Metadata.Id, old.Metadata.Id }));
    }
}
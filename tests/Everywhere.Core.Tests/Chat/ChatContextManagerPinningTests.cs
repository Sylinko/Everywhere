using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Storage;

namespace Everywhere.Core.Tests.Chat;

partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task Organize_WhenNewEmptyChatFollowsExhaustedHistory_KeepsItSearchableWithoutSaving()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(20);
        var root = manager.Current;
        await manager.RenameAsync(root.Data.Metadata, "needle draft");
        await PumpDispatcherAsync();
        manager.HistorySearchQuery = "needle";
        Assert.That(manager.HasMoreItems, Is.True);
        await session.LoadMoreAsync(1);
        Assert.That(Flatten(manager).Single(), Is.SameAs(root.Data.Metadata));

        await manager.SetPinnedAsync(root.Data.Metadata, true);
        await manager.ArchiveAsync(root.Data.Metadata);
        var page = await storage.ReadHistoryPageAsync(1);
        Assert.That(page.Items, Is.Empty);
        Assert.That(root.Data.Metadata.IsPinned, Is.False);
        Assert.That(root.Data.Metadata.IsArchived, Is.False);

        await root.DispatchAsync(() => root.Data.Add(new UserChatMessage("first message", [])));
        await manager.SetPinnedAsync(root.Data.Metadata, true);
        Assert.That((await storage.ReadHistoryPageAsync(1)).Items.Single().IsPinned, Is.True);
        Assert.That(manager.Current, Is.SameAs(root));
    }

    [AvaloniaTest]
    public async Task SetPinned_WhenSaveFails_PublishesOnlyAfterSuccessfulRetryWithoutSavingOtherChats()
    {
        var metadata = Metadata("target", 1);
        var storage = new TestChatContextStorage([metadata]);
        storage.Contexts[metadata.Id] = Data(metadata, new UserChatMessage("target", []));
        await using var manager = CreateManager(storage);
        var root = await manager.LoadChatContextAsync(metadata) ?? throw new AssertionException("Missing root.");
        using var unrelated = Data(new UserChatMessage("unrelated pending write", []));
        ChatStorageTestExtensions.AttachRoot(manager, unrelated);
        var unrelatedId = unrelated.Metadata.Id;
        storage.ContextSaver = batch =>
            batch.Metadata.Id == unrelatedId ? Task.FromException(new IOException("Unrelated write failed")) : Task.CompletedTask;
        storage.PinSetter = (_, _) => Task.FromException(new IOException("Pin write failed"));
        var originalDate = metadata.DateModified;

        Assert.ThrowsAsync<IOException>(() => manager.SetPinnedAsync(metadata, true));
        Assert.That(metadata.IsPinned, Is.False);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.PinSetter = async (_, _) =>
        {
            started.SetResult();
            await release.Task;
        };
        var pin = manager.SetPinnedAsync(metadata, true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(metadata.IsPinned, Is.False);
        release.SetResult();
        await pin;
        Assert.Multiple(() =>
        {
            Assert.That(metadata.IsPinned, Is.True);
            Assert.That(metadata.DateModified, Is.EqualTo(originalDate));
            Assert.That(root.Data.PersistenceDirtyState.HasChanges, Is.False);
        });
        Assert.That(await manager.LoadChatContextAsync(metadata), Is.SameAs(root));
        storage.ContextSaver = null;
        await manager.FlushPendingSavesAsync();
    }

    [AvaloniaTest]
    public async Task SetPinned_WhenHistoryIsPartlyVisible_RetainsIdentityAndDoesNotCountRescannedRows()
    {
        var rows = Enumerable.Range(1, 4).Select(index => Metadata($"row {index}", index)).ToArray();
        await using var manager = CreateManager(new TestChatContextStorage(rows));
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(2);
        await manager.SetPinnedAsync(rows[1], true);
        await PumpDispatcherAsync();
        Assert.That(manager.AllHistory[0].Kind, Is.EqualTo(ChatHistorySectionKind.Pinned));
        Assert.That(Flatten(manager)[0], Is.SameAs(rows[1]));
        Assert.That(Flatten(manager)[1], Is.SameAs(rows[0]));

        var result = await session.LoadMoreAsync(2);
        Assert.That(result.AddedItemCount, Is.EqualTo(2));
        Assert.That(Flatten(manager).Select(item => item.Id), Is.EquivalentTo(rows.Select(item => item.Id)));
        await manager.SetPinnedAsync(rows[1], false);
        await PumpDispatcherAsync();
        Assert.That(Flatten(manager), Is.EqualTo(rows));
        Assert.That(manager.AllHistory.All(section => section.Kind != ChatHistorySectionKind.Pinned), Is.True);
    }

    [AvaloniaTest]
    public async Task Search_WhenOrdinaryChatIsCached_ReadsMatchingColdPinsFirst()
    {
        var recent = Metadata("needle recent", 1);
        var excluded = Metadata("other", 100);
        excluded.IsPinned = true;
        var pinned = Metadata("needle old", 200);
        pinned.IsPinned = true;
        var storage = new TestChatContextStorage([recent, excluded, pinned]);
        storage.Contexts[recent.Id] = Data(recent, new UserChatMessage("recent", []));
        await using var manager = CreateManager(storage);
        await manager.LoadChatContextAsync(recent);
        manager.HistorySearchQuery = "needle";
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(1);
        Assert.That(Flatten(manager).Single(), Is.SameAs(pinned));
        await session.LoadMoreAsync(1);
        Assert.That(Flatten(manager), Is.EqualTo(new[] { pinned, recent }));
    }

    [AvaloniaTest]
    public async Task SetPinned_DuringContentSearch_RetargetsUnpublishedPageAndKeepsVisibleMatches()
    {
        var first = Metadata("needle first", 1);
        var second = Metadata("body match", 2);
        var last = Metadata("needle last", 3);
        var storage = new TestChatContextStorage([first, second, last]);
        storage.Contexts[second.Id] = Data(second, new UserChatMessage("needle", []));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        storage.ContextLoader = async (id, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return storage.Contexts[id];
        };
        await using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "needle";
        manager.HistorySearchIncludesContent = true;
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(1);
        var load = session.LoadMoreAsync(1).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.SetPinnedAsync(last, true);
        var result = await load.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(1));
            Assert.That(Flatten(manager), Is.EqualTo(new[] { last, first }));
            Assert.That(Flatten(manager)[1], Is.SameAs(first));
        });
    }

    [AvaloniaTest]
    public async Task ArchiveCurrent_WhenOlderChatIsPinned_SelectsMostRecentChat()
    {
        var recent = Metadata("recent", 1);
        var old = Metadata("old", 200);
        old.IsPinned = true;
        var storage = new TestChatContextStorage([old, recent]);
        storage.Contexts[recent.Id] = Data(recent, new UserChatMessage("recent", []));
        storage.Contexts[old.Id] = Data(old, new UserChatMessage("old", []));
        await using var manager = CreateManager(storage);
        var current = manager.Current;
        await current.DispatchAsync(() => current.Data.Add(new UserChatMessage("archive me", [])));
        await manager.ArchiveAsync(current.Data.Metadata);
        Assert.That(manager.Current.Data.Metadata, Is.SameAs(recent));
    }
}
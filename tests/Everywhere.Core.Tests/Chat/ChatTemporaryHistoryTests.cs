using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Chat;

partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task PersistChat_WhenEmpty_ChangesRetentionWithoutSavingOrReplacingTheContext()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        using var services = new ServiceCollection().BuildServiceProvider();
        var settings = new Settings(services);
        settings.ChatWindow.TemporaryChatMode = TemporaryChatMode.Always;
        await using var manager = new ChatContextManager(settings, storage, NullLogger<ChatContextManager>.Instance);
        var root = manager.CreateChat(isTemporary: true);
        var metadata = root.Data.Metadata;
        await manager.FlushPendingSavesAsync();
        Assert.That(metadata.ActualTopic, Is.EqualTo(Everywhere.Core.I18N.LocaleKey.ChatContext_Temporary.I18N()));

        await manager.PersistChatAsync(metadata);
        await manager.FlushPendingSavesAsync();
        Assert.That(metadata.IsTemporary, Is.False);
        Assert.That(manager.Current, Is.SameAs(root));
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.Zero);
        Assert.That(await db.Nodes.CountAsync(), Is.Zero);

        var regular = manager.CreateChat(isTemporary: false);
        Assert.That(regular.Data.Metadata.IsTemporary, Is.False, "Explicit retention overrides the main window's Always preference.");
        Assert.That(regular.Data.HasHistory, Is.False);
    }

    [AvaloniaTest]
    public async Task TemporaryChats_WhenSwitchedPinnedAndSaved_PreserveBranchesAndSurviveCleanupOnlyAfterSaving()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        var first = manager.CreateChat(isTemporary: true);
        var user = await first.DispatchAsync(() => first.Data.Add(new UserChatMessage("original", [])));
        var branch = await first.DispatchAsync(() => first.Data.CreateBranchOn(user, new UserChatMessage("edited", [])));
        await manager.FlushPendingSavesAsync();
        var second = manager.CreateChat(isTemporary: true);
        await second.DispatchAsync(() => second.Data.Add(new UserChatMessage("another lookup", [])));
        await manager.SetPinnedAsync(first.Data.Metadata, true);
        await manager.FlushPendingSavesAsync();
        await PumpDispatcherAsync();

        manager.HistoryView = ChatHistoryView.Temporary;
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(20);
        Assert.That(Flatten(manager), Is.EqualTo(new[] { first.Data.Metadata, second.Data.Metadata }));
        Assert.That(await manager.LoadChatContextAsync(first.Data.Metadata), Is.SameAs(first));
        Assert.That((await storage.ReadHistoryPageAsync(20)).Items, Has.Count.EqualTo(2));

        // Reopening from disk uses the same topology while leaving bodies lazy.
        using (var loaded = await storage.GetChatContextAsync(first.Data.Metadata))
        {
            Assert.That(loaded.FindNode(user.Id), Is.Not.Null);
            Assert.That(loaded.FindNode(branch.Id)?.IsMessageLoaded, Is.False);
            Assert.That(loaded.RootNode.ChoiceChildId, Is.EqualTo(branch.Id));
        }

        await manager.PersistChatAsync(first.Data.Metadata);
        await PumpDispatcherAsync();
        Assert.That(manager.Current, Is.SameAs(second));
        Assert.That(first.Data.FindNode(branch.Id), Is.SameAs(branch));
        Assert.That(first.Data.Metadata.IsPinned, Is.True);
        Assert.That(first.Data.Metadata.IsTemporary, Is.False);
        Assert.That(Flatten(manager), Is.EqualTo(new[] { second.Data.Metadata }));

        await manager.DisposeAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That((await db.Chats.SingleAsync()).Id, Is.EqualTo(first.Data.Metadata.Id));
        Assert.That(await db.Nodes.CountAsync(), Is.EqualTo(3));
    }

    [AvaloniaTest]
    public async Task PersistChat_InRecentHistory_KeepsVisibleItemsAndSectionsWithOrWithoutSearch()
    {
        foreach (var query in new string?[] { null, "needle" })
        {
            using var files = ChatTestDatabase.Create();
            var storage = new ChatTurnStorage(files.Factory);
            await using var manager = CreateManager(storage);
            var root = manager.CreateChat(isTemporary: true);
            await root.DispatchAsync(() =>
            {
                root.Data.Add(new UserChatMessage("message", []));
                root.Data.Metadata.Topic = "needle chat";
            });
            await manager.FlushPendingSavesAsync();
            await PumpDispatcherAsync();
            manager.HistorySearchQuery = query;
            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();
            var section = manager.AllHistory.Single();
            var collectionChanges = 0;
            section.Items.CollectionChanged += (_, _) => collectionChanges++;

            await manager.PersistChatAsync(root.Data.Metadata);
            await PumpDispatcherAsync();

            Assert.That(manager.AllHistory.Single(), Is.SameAs(section));
            Assert.That(section.Items.Single(), Is.SameAs(root.Data.Metadata));
            Assert.That(collectionChanges, Is.Zero);
            Assert.That(manager.HasMoreItems, Is.False);
        }
    }

    [AvaloniaTest]
    public async Task SaveTemporary_WhenCommitFailsThenOutputArrivesDuringRetry_PublishesAfterCommitAndRetainsNewRevision()
    {
        var storage = new TestChatContextStorage([]);
        await using var manager = CreateManager(storage);
        var root = manager.CreateChat(isTemporary: true);
        var message = new UserChatMessage("before", []);
        await root.DispatchAsync(() => root.Data.Add(message));
        await manager.FlushPendingSavesAsync();
        storage.ContextSaver = _ => Task.FromException(new IOException("disk unavailable"));
        Assert.ThrowsAsync<IOException>(() => manager.PersistChatAsync(root.Data.Metadata));
        Assert.That(root.Data.Metadata.IsTemporary, Is.True);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var batches = new List<ChatWriteBatch>();
        storage.ContextSaver = async batch =>
        {
            batches.Add(batch);
            if (batches.Count == 1)
            {
                started.SetResult();
                await release.Task;
            }
        };

        var saving = manager.PersistChatAsync(root.Data.Metadata);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(root.Data.Metadata.IsTemporary, Is.True);
        await root.DispatchAsync(() => message.Content = "during commit");
        release.SetResult();
        await saving;
        await manager.FlushPendingSavesAsync();
        Assert.That(root.Data.Metadata.IsTemporary, Is.False);
        Assert.That(batches.All(batch => !batch.Metadata.IsTemporary), Is.True);
        Assert.That(batches.Last().Nodes, Has.Count.EqualTo(1));
        Assert.That(root.Data.PersistenceDirtyState.HasChanges, Is.False);
    }

    [Test]
    public async Task Initialize_WhenPreviousSessionLeftTemporaryChats_RemovesOnlyThoseRowsIdempotently()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        using var temporary = Data(new UserChatMessage("temporary", []));
        temporary.Metadata.IsTemporary = true;
        await storage.SaveDataAsync(temporary);
        using var saved = Data(new UserChatMessage("keep", []));
        await storage.SaveDataAsync(saved);
        var initializer = new ChatV2StoreInitializer(files.Factory, null, NullLogger<ChatV2StoreInitializer>.Instance);
        await initializer.InitializeAsync(CancellationToken.None);
        await initializer.InitializeAsync(CancellationToken.None);
        await using var db = files.Factory.CreateDbContext();
        Assert.That((await db.Chats.SingleAsync()).Id, Is.EqualTo(saved.Metadata.Id));
        Assert.That(await db.Nodes.CountAsync(), Is.EqualTo(2));
    }
}
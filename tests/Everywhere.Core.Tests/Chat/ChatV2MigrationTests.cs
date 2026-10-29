using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Interop;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Views;
using Everywhere.ViewModels;
using NSubstitute;
using Everywhere.Chat.Plugins;
using Everywhere.I18N;
using Lucide.Avalonia;
using System.Text;
using Everywhere.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;

namespace Everywhere.Core.Tests.Chat;

public sealed class ChatV2MigrationTests
{
    [AvaloniaTest]
    public async Task ReadUserText_WhenCoveredAttachmentsAreUnknown_DoesNotDeserializeThem()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        using var data = new ChatData();
        var user = data.Add(new UserStrategyChatMessage("retained", [], new Strategy
        {
            Id = "test.strategy",
            NameKey = new DirectLocaleKey("Test strategy"),
        }));
        await new ChatTurnStorage(files.Factory).SaveDataAsync(data);
        // A user union with readable key 0 and an attachment union unsupported by this client.
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        writer.WriteArrayHeader(2);
        writer.Write((int)ChatMessageKind.UserStrategy);
        writer.WriteArrayHeader(2);
        writer.Write("retained");
        writer.WriteArrayHeader(1);
        writer.WriteArrayHeader(2);
        writer.Write(999);
        writer.WriteNil();
        writer.Flush();
        var payload = buffer.WrittenSpan.ToArray();
        await using (var db = files.Factory.CreateDbContext())
        {
            await db.Nodes.Where(row => row.Id == user.Id).ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Payload, payload));
        }

        var storage = new ChatTurnStorage(files.Factory);
        await using var cold = await storage.LoadContextAsync(data.Metadata.Id);
        var coldUser = cold.Data.SelectedTurns.Single().UserNode;
        Assert.That(coldUser.MessageKind, Is.EqualTo(ChatMessageKind.UserStrategy));
        Assert.That(await cold.ReadUserTextAsync(coldUser), Is.EqualTo("retained"));
        Assert.That(coldUser.IsMessageLoaded, Is.False);
    }

    [Test]
    public async Task UpgradeNotice_WhenImportIsCommitted_ReappearsUntilAcknowledgedAndKeepsLegacyByDefault()
    {
        using var files = new DatabaseFiles();
        await using (var source = await files.OpenLegacyAsync())
        {
            await InsertChatAsync(source, Guid.NewGuid());
        }

        await files.InitializeAsync();
        var upgrade = new ChatStoreUpgradeService(files.Factory, files.LegacyPath, Substitute.For<INativeHelper>());
        Assert.That(await upgrade.NeedsNoticeAsync(), Is.True);
        await files.InitializeAsync();
        Assert.That(await new ChatStoreUpgradeService(files.Factory, files.LegacyPath, Substitute.For<INativeHelper>()).NeedsNoticeAsync(), Is.True);
        await upgrade.AcknowledgeAsync();
        await upgrade.AcknowledgeAsync();
        Assert.That(await upgrade.NeedsNoticeAsync(), Is.False);
        Assert.That(File.Exists(files.LegacyPath), Is.True);
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.StoreInfo.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task UpgradeNotice_WhenTrashIsChosen_MovesOnlyLegacyFilesAndRemainsAcknowledged()
    {
        using var files = new DatabaseFiles();
        await using (var source = await files.OpenLegacyAsync())
        {
            await InsertChatAsync(source, Guid.NewGuid());
        }

        await files.InitializeAsync();
        var paths = new[] { files.LegacyPath, files.LegacyPath + "-wal", files.LegacyPath + "-shm" };
        await File.WriteAllTextAsync(paths[1], "pending WAL");
        await File.WriteAllTextAsync(paths[2], "shared memory");
        var attachment = files.LegacyPath + ".attachment";
        await File.WriteAllTextAsync(attachment, "shared attachment");
        var trash = Directory.CreateDirectory(files.LegacyPath + ".trash").FullName;
        var nativeHelper = Substitute.For<INativeHelper>();
        nativeHelper.MoveToTrashAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            foreach (var path in call.Arg<IReadOnlyList<string>>())
            {
                File.Move(path, Path.Combine(trash, Path.GetFileName(path)));
            }
            return Task.CompletedTask;
        });
        var upgrade = new ChatStoreUpgradeService(files.Factory, files.LegacyPath, nativeHelper);
        using var cancellation = new CancellationTokenSource();
        await upgrade.TrashLegacyDatabaseAsync(cancellation.Token);
        await nativeHelper.Received(1).MoveToTrashAsync(
            Arg.Is<IReadOnlyList<string>>(actual => actual.SequenceEqual(paths)), cancellation.Token);
        Assert.That(paths.All(path => File.Exists(Path.Combine(trash, Path.GetFileName(path)))), Is.True);
        Assert.That(File.Exists(attachment), Is.True);
        await upgrade.AcknowledgeAsync();
        await files.InitializeAsync();
        Assert.That(File.Exists(files.LegacyPath), Is.False);
        Assert.That(await upgrade.NeedsNoticeAsync(), Is.False);
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task UpgradeNotice_WhenTrashFailsOrIsCanceled_DoesNotDeleteOrRollBackAndCanAcknowledge(bool isCanceled)
    {
        using var files = new DatabaseFiles();
        await using (var source = await files.OpenLegacyAsync())
        {
            await InsertChatAsync(source, Guid.NewGuid());
        }

        await files.InitializeAsync();
        var wal = files.LegacyPath + "-wal";
        await File.WriteAllTextAsync(wal, "pending WAL");
        var recycled = files.LegacyPath + ".recycled";
        var nativeHelper = Substitute.For<INativeHelper>();
        using var cancellation = new CancellationTokenSource();
        nativeHelper.MoveToTrashAsync(Arg.Any<IReadOnlyList<string>>(), cancellation.Token).Returns(_ =>
        {
            File.Move(files.LegacyPath, recycled);
            if (isCanceled)
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            }
            return Task.FromException(new IOException("Trash is unavailable for the remaining file."));
        });
        var upgrade = new ChatStoreUpgradeService(files.Factory, files.LegacyPath, nativeHelper);
        var expectedException = isCanceled ? typeof(OperationCanceledException) : typeof(IOException);
        await Assert.ThatAsync(() => upgrade.TrashLegacyDatabaseAsync(cancellation.Token), Throws.InstanceOf(expectedException));
        Assert.That(File.Exists(files.LegacyPath), Is.False);
        Assert.That(File.Exists(recycled), Is.True);
        Assert.That(await File.ReadAllTextAsync(wal), Is.EqualTo("pending WAL"));
        await upgrade.AcknowledgeAsync();
        Assert.That(await upgrade.NeedsNoticeAsync(), Is.False);
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Archive_WhenConversationIsRunning_PreservesWorkAndSupportsRestoreAndPermanentDeletion()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        var user = new UserChatMessage("question", []);
        data.Add(user);
        await storage.SaveDataAsync(data);
        using var manager = new ChatContextManager(new Settings(new ServiceCollection().BuildServiceProvider()), storage, NullLogger<ChatContextManager>.Instance);
        var runtime = ChatStorageTestExtensions.AttachRoot(manager, data);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(20);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = runtime.ExecuteAsync(async cancellationToken =>
        {
            started.SetResult();
            await release.Task.WaitAsync(cancellationToken);
            await runtime.DispatchAsync(() => data.Add(new UserChatMessage("completed after archive", [])));
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await manager.ArchiveAsync(data.Metadata);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.That(runtime.IsBusy, Is.True);
        Assert.That(manager.AllHistory.SelectMany(group => group.MetadataList), Is.Empty);
        manager.IsArchiveView = true;
        await session.LoadMoreAsync(20);
        Assert.That(manager.AllHistory.SelectMany(group => group.MetadataList).Single(), Is.SameAs(data.Metadata));
        release.SetResult();
        await operation;
        await manager.FlushPendingSavesAsync();
        Assert.That(data.Metadata.IsArchived, Is.True);

        await manager.RestoreAsync(data.Metadata);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.That(manager.AllHistory.SelectMany(group => group.MetadataList), Is.Empty);
        manager.IsArchiveView = false;
        await session.LoadMoreAsync(20);
        Assert.That(manager.AllHistory.SelectMany(group => group.MetadataList).Single(), Is.SameAs(data.Metadata));

        await manager.ArchiveAsync(data.Metadata);
        await manager.DeletePermanentlyAsync(data.Metadata);
        user.Content = "late callback";
        await manager.FlushPendingSavesAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.Zero);
        Assert.That(await db.Nodes.CountAsync(), Is.Zero);
    }

    [AvaloniaTest]
    public async Task Export_WhenSelectedHistoryIsCold_LoadsItsTurnsWithoutLoadingOtherUserBranches()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        var user = data.Add(new UserChatMessage("selected question", []));
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageTextSpan("selected answer"));
        data.Add(assistant);
        var other = data.CreateBranchOn(user, new UserChatMessage("other question", []));
        data.SelectChild(data.RootNode, user);
        await storage.SaveDataAsync(data);

        await using var cold = await new ChatTurnStorage(files.Factory).LoadContextAsync(data.Metadata.Id);
        using var stream = new MemoryStream();
        await ChatDataExporter.ExportAsMarkdown(cold, stream);
        var markdown = Encoding.UTF8.GetString(stream.ToArray());
        Assert.That(markdown, Does.Contain("selected question").And.Contain("selected answer"));
        Assert.That(markdown, Does.Not.Contain("other question"));
        Assert.That(cold.Data.FindNode(other.Id)?.IsMessageLoaded, Is.False);
    }

    [Test]
    public async Task Initialize_WhenLegacyProjectionOmitsChildAttachments_RecoversReferencesWithoutRewritingPayload()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var chatId = Guid.NewGuid();
        var toolId = Guid.NewGuid();
        var child = new ChatData();
        child.Add(new UserChatMessage("child", [new FileAttachment(new DirectLocaleKey("file"), "missing.txt", "child-file", "text/plain")]));
        using var tool = new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("tool"));
        tool.DisplaySink.AppendSubagent(child);
        var payload = MessagePackSerializer.Serialize<ChatMessage>(tool);
        await InsertChatAsync(source, chatId);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, toolId, MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()));
        await InsertNodeAsync(source, chatId, toolId, Guid.Empty, null, payload);

        await files.InitializeAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That((await db.NodeBlobs.SingleAsync()).BlobSha256, Is.EqualTo("child-file"));
        Assert.That((await db.Nodes.SingleAsync(node => node.Id == toolId)).Payload, Is.EqualTo(payload));

        var storage = new ChatTurnStorage(files.Factory);
        await using var root = await storage.LoadContextAsync(chatId);
        await root.EnsureTurnLoadedAsync(null);
        var restoredNode = root.Data.FindNode(toolId) ?? throw new AssertionException("Missing migrated tool.");
        var restoredTool = (FunctionCallChatMessage)restoredNode.Message;
        var restoredChild = ((ChatPluginSubagentDisplayBlock)restoredTool.DisplayBlocks.Single()).ChatData;
        var nested = root.GetNested(restoredChild);
        Assert.That(nested.Data, Is.SameAs(restoredChild));
        Assert.That(nested.Parent, Is.SameAs(root));
        Assert.That(nested.WorkingDirectory, Is.EqualTo(((ChatContext)root).WorkingDirectory));
    }

    [AvaloniaTest]
    public async Task MetadataPages_WhenSortKeysTie_PreserveCursorsCanonicalIdentityAndArchiveState()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var first = new ChatData();
        using var second = new ChatData();
        using var third = new ChatData();
        foreach (var context in new[] { first, second, third })
        {
            context.Add(new UserChatMessage("message", []));
            context.Metadata.DateModified = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            context.Metadata.Topic = ReferenceEquals(context, first) ? null : "same";
            await storage.SaveDataAsync(context);
        }

        var coldStorage = new ChatTurnStorage(files.Factory);
        var expected = new[] { first.Metadata.Id, second.Metadata.Id, third.Metadata.Id }.OrderDescending().ToArray();
        var actual = new List<ChatMetadata>();
        var cursor = default(Guid?);
        while (true)
        {
            var page = await ReadPageAsync(1, cursor);
            if (page.Count == 0)
            {
                break;
            }

            actual.Add(page[0]);
            cursor = page[0].Id;
            Assert.That(actual.Count, Is.LessThanOrEqualTo(3), "The cursor repeated a page.");
        }
        Assert.That(actual.Select(item => item.Id), Is.EqualTo(expected));

        var metadata = (await ReadPageAsync(10)).Single(item => item.Id == first.Metadata.Id);
        using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()), coldStorage, NullLogger<ChatContextManager>.Instance);
        var reads = await Task.WhenAll(manager.LoadChatContextAsync(metadata), manager.LoadChatContextAsync(metadata));
        var cold = reads[0] ?? throw new AssertionException("Missing conversation.");
        Assert.That(cold.Data.Metadata, Is.SameAs(metadata));
        Assert.That(reads[1], Is.SameAs(cold));
        Assert.That(cold.Data.SelectedTurns[0].UserNode.IsMessageLoaded, Is.False);
        await manager.ArchiveAsync(metadata);
        Assert.That(metadata.IsArchived, Is.True);
        Assert.That(await ReadPageAsync(10), Has.Count.EqualTo(2));
        var archived = new List<ChatMetadata>();
        await foreach (var item in coldStorage.QueryChatContextsAsync(10, isArchived: true))
        {
            archived.Add(item);
        }

        Assert.That(archived.Single().Id, Is.EqualTo(metadata.Id));
        Assert.That(archived.Single().IsArchived, Is.True);
        Assert.That((await manager.LoadChatContextAsync(archived.Single()))?.Data.Metadata, Is.SameAs(metadata));
        await manager.RenameAsync(metadata, "renamed in archive");
        await manager.FlushPendingSavesAsync();
        Assert.That(metadata.Topic, Is.EqualTo("renamed in archive"));
        Assert.That(metadata.IsArchived, Is.True);
        await manager.RestoreAsync(metadata);
        Assert.That(metadata.IsArchived, Is.False);
        Assert.That(await ReadPageAsync(10), Has.Count.EqualTo(3));

        async Task<List<ChatMetadata>> ReadPageAsync(int take, Guid? cursor = null)
        {
            var page = new List<ChatMetadata>();
            await foreach (var item in coldStorage.QueryChatContextsAsync(take, cursor))
            {
                page.Add(item);
            }

            return page;
        }
    }

    [AvaloniaTest]
    public async Task NavigationAndSearch_WhenHistoryIsCold_LoadSelectedTurnsOnlyAndKeepEntryIdentity()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        data.Add(new UserChatMessage("needle question", []));
        data.Add(new VisualContextResetChatMessage("reset", Guid.NewGuid()));
        var assistant = new AssistantChatMessage();
        assistant.AddSpan(new AssistantChatMessageTextSpan("needle answer"));
        var assistantNode = data.Add(assistant);
        var second = data.Add(new UserChatMessage("second", []));
        var alternative = data.CreateBranchOn(second, new UserChatMessage("needle alternative", []));
        data.SelectChild(assistantNode, second);
        await storage.SaveDataAsync(data);

        await using var cold = await new ChatTurnStorage(files.Factory).LoadContextAsync(data.Metadata.Id);
        using var index = new ChatTurnNavigationIndex(cold);
        var firstEntry = index.Turns[0];
        Assert.That(index.Turns, Has.Count.EqualTo(2));
        Assert.That(cold.Data.Items.Where(node => node.MessageKind != ChatMessageKind.Root).All(node => !node.IsMessageLoaded), Is.True);

        var manager = Substitute.For<IChatContextManager>();
        manager.Current.Returns(cold);
        using var search = new ChatTextSearchViewModel(manager) { Query = "needle" };
        search.OpenSearchCommand.Execute(null);
        await WaitForSearchAsync();
        Assert.That(search.MatchCount, Is.EqualTo(2));
        Assert.That(cold.Data.FindNode(alternative.Id)?.IsMessageLoaded, Is.False);

        var parent = cold.Data.FindNode(assistantNode.Id) ?? throw new AssertionException("Missing branch parent.");
        cold.Data.SelectChild(parent, cold.Data.FindNode(alternative.Id));
        await WaitForSearchAsync();
        Assert.That(index.Turns[0], Is.SameAs(firstEntry));
        Assert.That(index.Turns[1].Node.Id, Is.EqualTo(alternative.Id));
        Assert.That(search.MatchCount, Is.EqualTo(3));

        async Task WaitForSearchAsync()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                await Task.Delay(10);
                Dispatcher.UIThread.RunJobs();
                if (!search.IsBusy)
                {
                    return;
                }
            }

            Assert.Fail("Search did not finish loading the selected branch.");
        }
    }

    [AvaloniaTest]
    public async Task Save_WhenOnlyChoiceChanges_DoesNotTouchPayloadOrLoadHistory()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        var original = data.Add(new UserChatMessage("original", []));
        var alternative = data.CreateBranchOn(original, new UserChatMessage("alternative", []));
        await storage.SaveDataAsync(data);

        await using (var db = files.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectPayloadUpdate BEFORE UPDATE OF Payload ON Nodes
                BEGIN SELECT RAISE(ABORT, 'unexpected payload rewrite'); END;
                """);
        }

        var coldStorage = new ChatTurnStorage(files.Factory);
        await using var cold = await coldStorage.LoadContextAsync(data.Metadata.Id);
        var coldOriginal = cold.Data.FindNode(original.Id) ?? throw new AssertionException("Missing original branch.");
        cold.Data.SelectChild(cold.Data.RootNode, coldOriginal);
        await coldStorage.SaveDataAsync(cold.Data);
        Assert.That(coldOriginal.IsMessageLoaded, Is.False);
        Assert.That(cold.Data.FindNode(alternative.Id)?.IsMessageLoaded, Is.False);
        Assert.That(cold.Data.PersistenceDirtyState.HasChanges, Is.False);

        await using var saved = files.Factory.CreateDbContext();
        Assert.That((await saved.Nodes.SingleAsync(row => row.ParentId == null)).ChoiceChildId, Is.EqualTo(original.Id));
    }

    [AvaloniaTest]
    public async Task Save_WhenTransactionFails_KeepsDirtyNodesAndRetriesWholeBatch()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        await using (var db = files.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectNode BEFORE INSERT ON Nodes
                BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
                """);
        }

        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        data.Add(new UserChatMessage("keep this", []));
        await Assert.ThatAsync(() => storage.SaveDataAsync(data), Throws.TypeOf<SqliteException>());
        Assert.That(data.PersistenceDirtyState.HasChanges, Is.True);
        await using (var db = files.Factory.CreateDbContext())
        {
            Assert.That(await db.Chats.CountAsync(), Is.Zero);
            Assert.That(await db.Nodes.CountAsync(), Is.Zero);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectNode");
        }

        await storage.SaveDataAsync(data);
        Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
        await using var restored = files.Factory.CreateDbContext();
        Assert.That(await restored.Nodes.CountAsync(), Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task Delete_WhenDatabaseRejectsIt_ReleasesReservationAndRetainsRowsForRetry()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        data.Add(new UserChatMessage("retained", []));
        await storage.SaveDataAsync(data);
        using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()), storage, NullLogger<ChatContextManager>.Instance);
        var runtime = ChatStorageTestExtensions.AttachRoot(manager, data);
        await using (var db = files.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectDelete BEFORE DELETE ON Chats BEGIN SELECT RAISE(ABORT, 'delete failed'); END");
        }

        await Assert.ThatAsync(() => manager.DeletePermanentlyAsync(data.Metadata), Throws.TypeOf<SqliteException>());
        await runtime.ExecuteAsync(_ => Task.CompletedTask);
        await using (var db = files.Factory.CreateDbContext())
        {
            Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
            Assert.That(await db.Nodes.CountAsync(), Is.EqualTo(2));
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectDelete");
        }
        await manager.DeletePermanentlyAsync(data.Metadata);
        await Assert.ThatAsync(() => runtime.ExecuteAsync(_ => Task.CompletedTask), Throws.TypeOf<InvalidOperationException>());
        await using var deleted = files.Factory.CreateDbContext();
        Assert.That(await deleted.Nodes.CountAsync(), Is.Zero);
    }

    [AvaloniaTest]
    public async Task Save_WhenArchived_RemainsArchivedAndPermanentDeletionRejectsQueuedSave()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        var storage = new ChatTurnStorage(files.Factory);
        using var data = new ChatData();
        var message = new UserChatMessage("first", []);
        data.Add(message);
        await storage.SaveDataAsync(data);
        await storage.ArchiveAsync(data.Metadata.Id);
        message.Content = "later output";
        await storage.SaveDataAsync(data);
        await using (var db = files.Factory.CreateDbContext())
        {
            Assert.That((await db.Chats.SingleAsync()).ArchivedAt, Is.Not.Null);
        }

        using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()), storage, NullLogger<ChatContextManager>.Instance);
        var runtime = ChatStorageTestExtensions.AttachRoot(manager, data);
        await runtime.DispatchAsync(() => message.Content = "queued before deletion");
        await manager.DeletePermanentlyAsync(data.Metadata);
        message.Content = "stale producer after deletion";
        await manager.FlushPendingSavesAsync();
        await using var deleted = files.Factory.CreateDbContext();
        Assert.That(await deleted.Chats.CountAsync(), Is.Zero);
        Assert.That(await deleted.Nodes.CountAsync(), Is.Zero);
    }

    [AvaloniaTest]
    public async Task ReadHistory_WhenSummaryCoversOldTurns_LoadsRetainedUserTextWithoutCoveredAssistant()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var chatId = Guid.NewGuid();
        var oldUserId = Guid.NewGuid();
        var oldAssistantId = Guid.NewGuid();
        var currentUserId = Guid.NewGuid();
        var summaryId = Guid.NewGuid();
        var currentAssistantId = Guid.NewGuid();
        await InsertChatAsync(source, chatId);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, oldUserId, MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()));
        await InsertNodeAsync(source, chatId, oldUserId, Guid.Empty, oldAssistantId, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("retained instruction", [])));
        // Deliberately unreadable body: topology inspection and retained-user reads must not deserialize it.
        await InsertNodeAsync(source, chatId, oldAssistantId, oldUserId, currentUserId, [0x92, 0x01, 0xc0]);
        await InsertNodeAsync(source, chatId, currentUserId, oldAssistantId, summaryId, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("current request", [])));
        var summary = new ContextCompressionChatMessage(oldAssistantId, "model", ContextCompressionTrigger.Manual, null, null);
        summary.Complete("prior decisions", false, DateTimeOffset.UtcNow);
        await InsertNodeAsync(source, chatId, summaryId, currentUserId, currentAssistantId, MessagePackSerializer.Serialize<ChatMessage>(summary));
        await InsertNodeAsync(source, chatId, currentAssistantId, summaryId, null, MessagePackSerializer.Serialize<ChatMessage>(new AssistantChatMessage()));
        await files.InitializeAsync();

        var storage = new ChatTurnStorage(files.Factory);
        await using var context = await storage.LoadContextAsync(chatId);
        var assistant = context.Data.FindNode(currentAssistantId) ?? throw new AssertionException("Missing assistant.");
        Assert.That(assistant.IsMessageLoaded, Is.False);

        var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(context, context.Data.GetPathTo(assistant), 0, 10000);
        Assert.Multiple(() =>
        {
            Assert.That(selection.Messages.OfType<UserChatMessage>().Select(message => message.Content),
                Is.EqualTo(new[] { "retained instruction", "current request" }));
            Assert.That(selection.Messages.OfType<ContextCompressionChatMessage>().Single().Summary, Is.EqualTo("prior decisions"));
            Assert.That(context.Data.FindNode(oldAssistantId)?.IsMessageLoaded, Is.False);
            Assert.That(context.Data.FindNode(oldUserId)?.IsMessageLoaded, Is.False);
            Assert.That(context.Data.FindNode(currentAssistantId), Is.SameAs(assistant));
            Assert.That(assistant.IsMessageLoaded, Is.True);
            Assert.That(context.Data.PersistenceDirtyState.HasChanges, Is.False);
        });
    }

    [Test]
    public async Task Initialize_WhenSourceHasUncheckpointedWal_ImportsOnceAndKeepsSourceUnchanged()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var chatId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var payload = MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("kept verbatim", []));
        await InsertChatAsync(source, chatId);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, null, MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()));
        await InsertNodeAsync(source, chatId, userId, Guid.Empty, null, payload);
        Assert.That(File.Exists(files.LegacyPath + "-wal"), Is.True);

        await files.InitializeAsync();
        await files.InitializeAsync();

        await using var db = files.Factory.CreateDbContext();
        var rows = await db.Nodes.OrderBy(row => row.MessageKind).ToArrayAsync();
        var info = await db.StoreInfo.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(rows, Has.Length.EqualTo(2));
            Assert.That(rows[0].Id, Is.EqualTo(Guid.Empty));
            Assert.That(rows[0].ChoiceChildId, Is.EqualTo(userId));
            Assert.That(rows[1].TurnId, Is.EqualTo(userId));
            Assert.That(rows[1].Payload, Is.EqualTo(payload));
            Assert.That(info.LegacyImportedAt, Is.Not.Null);
            Assert.That(info.UpgradeNoticeAcknowledgedAt, Is.Null);
        });

        await using var sourceRead = source.CreateCommand();
        sourceRead.CommandText = "SELECT ChoiceChildId FROM Nodes WHERE ParentId IS NULL";
        Assert.That(await sourceRead.ExecuteScalarAsync(), Is.EqualTo(DBNull.Value));
    }

    [TestCase("UnsupportedKind")]
    [TestCase("TruncatedPayload")]
    [TestCase("DisconnectedNode")]
    [TestCase("InvalidPayload")]
    public async Task Initialize_WhenChatConversionFails_SkipsWholeChatAndLogsWarningOnce(string failure)
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var beforeId = Guid.NewGuid();
        var afterId = Guid.NewGuid();
        await InsertChatAsync(source, beforeId);
        var chatId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await InsertChatAsync(source, chatId);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, null, MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()));
        var file = new FileAttachment(new DirectLocaleKey("file"), "file.txt", "skipped-reference", "text/plain");
        await InsertNodeAsync(source, chatId, userId, Guid.Empty, null, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("before failure", [file])));
        var payload = failure switch
        {
            "UnsupportedKind" => new byte[] { 0x92, 0x7f, 0xc0 },
            "TruncatedPayload" => new byte[] { 0x92 },
            "InvalidPayload" => new byte[] { 0x92, (byte)ChatMessageKind.User, 0x91, 0x01 },
            _ => MessagePackSerializer.Serialize<ChatMessage>(new AssistantChatMessage()),
        };
        var parentId = failure == "DisconnectedNode" ? Guid.NewGuid() : userId;
        await InsertNodeAsync(source, chatId, Guid.NewGuid(), parentId, null, payload);
        await InsertChatAsync(source, afterId);

        await files.InitializeAsync();
        await files.InitializeAsync();

        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.Select(chat => chat.Id).ToArrayAsync(), Is.EquivalentTo(new[] { beforeId, afterId }));
        Assert.That(await db.Nodes.CountAsync(), Is.EqualTo(2));
        Assert.That(await db.Nodes.AnyAsync(node => node.ChatContextId == chatId), Is.False);
        Assert.That(await db.NodeBlobs.CountAsync(), Is.Zero);
        Assert.That(await db.StoreInfo.CountAsync(), Is.EqualTo(1));
        var warning = files.Logger.ReceivedCalls().Single(call => call.GetMethodInfo().Name == "Log").GetArguments();
        Assert.Multiple(() =>
        {
            Assert.That(warning[0], Is.EqualTo(LogLevel.Warning));
            Assert.That(warning[2]?.ToString(), Does.Contain(chatId.ToString()));
            Assert.That(warning[3], Is.InstanceOf<Exception>());
        });
    }

    [Test]
    public async Task Initialize_WhenEveryChatIsUnreadable_CompletesAndKeepsLegacyTrashAvailable()
    {
        using var files = new DatabaseFiles();
        await using (var source = await files.OpenLegacyAsync())
        {
            var chatId = Guid.NewGuid();
            await InsertChatAsync(source, chatId);
            await InsertNodeAsync(source, chatId, Guid.Empty, null, null, [0x92, 0x7f, 0xc0]);
            await files.InitializeAsync();
        }

        await files.InitializeAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.Zero);
        Assert.That(await db.StoreInfo.CountAsync(), Is.EqualTo(1));
        var nativeHelper = Substitute.For<INativeHelper>();
        var upgrade = new ChatStoreUpgradeService(files.Factory, files.LegacyPath, nativeHelper);
        Assert.That(await upgrade.NeedsNoticeAsync(), Is.True);
        await upgrade.TrashLegacyDatabaseAsync();
        await nativeHelper.Received(1).MoveToTrashAsync(
            Arg.Is<IReadOnlyList<string>>(paths => paths.Contains(files.LegacyPath)), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Initialize_WhenDatabaseWriteFails_RollsBackRowsAndMarkerThenRetries()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        await InsertChatAsync(source, Guid.NewGuid());
        await using (var db = files.Factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailNodeInsert BEFORE INSERT ON Nodes BEGIN SELECT RAISE(ABORT, 'test write failure'); END");
        }

        await Assert.ThatAsync(files.InitializeAsync, Throws.TypeOf<SqliteException>());
        await using (var db = files.Factory.CreateDbContext())
        {
            Assert.That(await db.Chats.CountAsync(), Is.Zero);
            Assert.That(await db.StoreInfo.CountAsync(), Is.Zero);
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailNodeInsert");
        }

        await files.InitializeAsync();
        await using var restored = files.Factory.CreateDbContext();
        Assert.That(await restored.Nodes.CountAsync(), Is.EqualTo(1));
        Assert.That(await restored.StoreInfo.CountAsync(), Is.EqualTo(1));
        Assert.That(files.Logger.ReceivedCalls(), Is.Empty);
    }

    [Test]
    public async Task Initialize_WhenArchivedWithMissingBlob_PreservesPayloadAndReference()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var chatId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await InsertChatAsync(source, chatId, isDeleted: true);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, userId, MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()), true);
        await InsertNodeAsync(source, chatId, userId, Guid.Empty, null, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("archived", [])), true);
        await using var reference = source.CreateCommand();
        reference.CommandText = "INSERT INTO NodeBlobs VALUES ($chat, $node, 0, 'missing-file')";
        reference.Parameters.AddWithValue("$chat", chatId);
        reference.Parameters.AddWithValue("$node", userId);
        await reference.ExecuteNonQueryAsync();

        await files.InitializeAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That((await db.Chats.SingleAsync()).ArchivedAt, Is.Not.Null);
        Assert.That(await db.Nodes.CountAsync(), Is.EqualTo(2));
        Assert.That((await db.NodeBlobs.SingleAsync()).BlobSha256, Is.EqualTo("missing-file"));
        Assert.That(await db.Blobs.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task Initialize_WhenChoiceIsInvalid_EndsBranchAndPreservesSiblingOrder()
    {
        using var files = new DatabaseFiles();
        await using var source = await files.OpenLegacyAsync();
        var chatId = Guid.NewGuid();
        var firstId = Guid.Parse("11111111-1111-7111-8000-000000000001");
        var secondId = Guid.Parse("11111111-1111-7111-8000-000000000002");
        await InsertChatAsync(source, chatId);
        await InsertNodeAsync(source, chatId, Guid.Empty, null, Guid.NewGuid(), MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage()));
        await InsertNodeAsync(source, chatId, secondId, Guid.Empty, null, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("second", [])));
        await InsertNodeAsync(source, chatId, firstId, Guid.Empty, null, MessagePackSerializer.Serialize<ChatMessage>(new UserChatMessage("first", [])));

        await files.InitializeAsync();
        await using var db = files.Factory.CreateDbContext();
        var root = await db.Nodes.SingleAsync(row => row.ParentId == null);
        var children = await db.Nodes.Where(row => row.ParentId != null).OrderBy(row => row.SiblingOrder).ToArrayAsync();
        Assert.Multiple(() =>
        {
            Assert.That(root.ChoiceChildId, Is.Null);
            Assert.That(children.Select(row => row.Id), Is.EqualTo(new[] { firstId, secondId }));
            Assert.That(children.Select(row => row.SiblingOrder), Is.EqualTo(new long[] { 0, 1 }));
        });
    }

    [Test]
    public async Task Initialize_WhenNewInstall_MarksInitializedWithoutLegacyNotice()
    {
        using var files = new DatabaseFiles();
        await files.InitializeAsync();
        await files.InitializeAsync();
        await using var db = files.Factory.CreateDbContext();
        var info = await db.StoreInfo.SingleAsync();
        Assert.That(info.LegacyImportedAt, Is.Null);
        Assert.That(await db.Chats.CountAsync(), Is.Zero);
    }

    private static async Task InsertChatAsync(SqliteConnection source, Guid id, bool isDeleted = false)
    {
        await using var command = source.CreateCommand();
        command.CommandText = "INSERT INTO Chats VALUES ($id, 'legacy', 100, 200, $deleted)";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$deleted", isDeleted);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertNodeAsync(
        SqliteConnection source, Guid chatId, Guid id, Guid? parentId, Guid? choiceId, byte[] payload, bool isDeleted = false)
    {
        await using var command = source.CreateCommand();
        command.CommandText = "INSERT INTO Nodes VALUES ($chat, $id, $parent, $choice, $payload, 100, 200, $deleted)";
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$parent", (object?)parentId ?? DBNull.Value);
        command.Parameters.AddWithValue("$choice", (object?)choiceId ?? DBNull.Value);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$deleted", isDeleted);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class DatabaseFiles : IDisposable
    {
        public string LegacyPath => Path.Combine(_directory, "chat.db");
        public TestDbFactory Factory { get; }
        public ILogger<ChatV2StoreInitializer> Logger { get; } = Substitute.For<ILogger<ChatV2StoreInitializer>>();

        private readonly string _directory = Path.Combine(Path.GetTempPath(), "Everywhere-ChatTurn-" + Guid.NewGuid());

        public DatabaseFiles()
        {
            Directory.CreateDirectory(_directory);
            Factory = new TestDbFactory(Path.Combine(_directory, "chat-v2.db"));
        }

        public Task InitializeAsync()
        {
            return new ChatV2StoreInitializer(Factory, LegacyPath, Logger).InitializeAsync(CancellationToken.None);
        }

        public async Task<SqliteConnection> OpenLegacyAsync()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LegacyPath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA wal_autocheckpoint=0;
                CREATE TABLE Chats (Id TEXT PRIMARY KEY, Topic TEXT, CreatedAt INTEGER, UpdatedAt INTEGER, IsDeleted INTEGER);
                CREATE TABLE Nodes (ChatContextId TEXT, Id TEXT, ParentId TEXT, ChoiceChildId TEXT, Payload BLOB, CreatedAt INTEGER, UpdatedAt INTEGER, IsDeleted INTEGER);
                CREATE TABLE NodeBlobs (ChatContextId TEXT, ChatNodeId TEXT, "Index" INTEGER, BlobSha256 TEXT);
                CREATE TABLE Blobs (Sha256 TEXT, LocalPath TEXT, MimeType TEXT, Size INTEGER, CreatedAt INTEGER, LastAccessAt INTEGER);
                """;
            await command.ExecuteNonQueryAsync();
            return connection;
        }

        public void Dispose()
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestDbFactory(string path) : IDbContextFactory<ChatV2DbContext>
    {
        public ChatV2DbContext CreateDbContext()
        {
            var connectionString = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, ForeignKeys = true }.ToString();
            return new ChatV2DbContext(new DbContextOptionsBuilder<ChatV2DbContext>().UseSqlite(connectionString).Options);
        }
    }
}
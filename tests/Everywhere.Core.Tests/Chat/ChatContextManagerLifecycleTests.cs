using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Configuration;
using MessagePack;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task RootRetention_WhenSaveFails_RetainsDataUntilRetryThenAllowsCollection()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var reference = OpenUnselectedRoot(manager);
        storage.ContextSaver = _ => Task.FromException(new IOException("disk unavailable"));
        await Assert.ThatAsync(() => manager.FlushPendingSavesAsync(), Throws.TypeOf<AggregateException>());
        Collect();
        Assert.That(reference.TryGetTarget(out _), Is.True, "Pending changes must retain their runtime.");

        storage.ContextSaver = _ => Task.CompletedTask;
        await manager.FlushPendingSavesAsync();
        await PumpDispatcherAsync();
        Collect();
        Assert.That(reference.TryGetTarget(out _), Is.False, "An idle, saved root must not be retained by the registry.");

        static void Collect()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<RootChatContext> OpenUnselectedRoot(ChatContextManager manager)
    {
        var data = Data(new UserChatMessage("pending", []));
        var dataRuntime = ChatStorageTestExtensions.AttachRoot(manager, data);
        return new WeakReference<RootChatContext>(dataRuntime.Root);
    }

    [AvaloniaTest]
    public async Task RootRetention_WhenNestedWorkIsCanceled_RemainsStrongThroughItsFinalMutation()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var root = manager.Current;
        var child = root.CreateNested("child");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backgroundRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var background = root.RunBackgroundAsync(_ => backgroundRelease.Task);
        var operation = child.ExecuteAsync(async token =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                finishing.SetResult();
                await release.Task;
                await child.DispatchAsync(() => child.Data.Add(new UserChatMessage("stopped", [])));
            }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            child.Cancel();
            await finishing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            backgroundRelease.SetResult();
            await background;
            Assert.That(root.Reference.IsActive, Is.True);
            Assert.That(child.WorkingDirectory, Is.EqualTo(((ChatContext)root).WorkingDirectory));
            Assert.That(root.GetNested(child.Data), Is.SameAs(child));
        }
        finally
        {
            backgroundRelease.TrySetResult();
            release.SetResult();
            await Assert.ThatAsync(async () => await operation, Throws.InstanceOf<OperationCanceledException>());
        }

        Assert.That(root.Reference.IsActive, Is.False);
        Assert.That(((UserChatMessage)child.Data.Items[^1].Message).Content, Is.EqualTo("stopped"));
    }

    [AvaloniaTest]
    public async Task Cancel_WhenCallbackOutlivesBody_KeepsForegroundAdmissionUntilCallbackFinishes()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var root = manager.Current;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = root.ExecuteAsync(async token =>
        {
            token.Register(() =>
            {
                callbackStarted.SetResult();
                release.Task.GetAwaiter().GetResult();
            });
            var waiting = Task.Delay(Timeout.InfiniteTimeSpan, token);
            started.SetResult();
            try
            {
                await waiting;
            }
            finally
            {
                bodyFinished.SetResult();
            }
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            root.Cancel();
            await Task.WhenAll(callbackStarted.Task, bodyFinished.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(operation.IsCompleted, Is.False);
            Assert.That(root.IsBusy, Is.True);
            Assert.That(root.Reference.IsActive, Is.True);
            Assert.Throws<InvalidOperationException>(() => root.ExecuteAsync(_ => Task.CompletedTask));
        }
        finally
        {
            release.SetResult();
            await Assert.ThatAsync(async () => await operation, Throws.InstanceOf<OperationCanceledException>());
        }

        Assert.That(root.IsBusy, Is.False);
        Assert.That(root.Reference.IsActive, Is.False);
    }

    [AvaloniaTest]
    public async Task LoadTurn_WhenOneReaderCancels_SharesIoAndInstallsIntoTheExistingNode()
    {
        var metadata = Metadata("stored", 1);
        var rootNode = new ChatMessageNode(Guid.Empty, new RootChatMessage());
        var userNode = new ChatMessageNode(Guid.NewGuid(), ChatMessageKind.User, metadata.DateCreated, metadata.DateModified);
        rootNode.AddChild(userNode.Id);
        rootNode.SetChoiceChild(userNode.Id);
        var data = new ChatData(metadata, [userNode], rootNode);
        var loader = Substitute.For<IChatTurnLoader>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodies = new TaskCompletionSource<IReadOnlyDictionary<Guid, ChatMessage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadingToken = CancellationToken.None;
        loader.LoadMessagesAsync(metadata.Id, userNode.Id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            loadingToken = call.ArgAt<CancellationToken>(2);
            started.SetResult();
            return bodies.Task.WaitAsync(loadingToken);
        });
        await using var runtime = new RootChatContext(data, loader);
        var turn = data.SelectedTurns.Single();
        using var cancellation = new CancellationTokenSource();
        var first = runtime.EnsureTurnLoadedAsync(turn, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = runtime.EnsureTurnLoadedAsync(turn);
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await first, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(loadingToken.IsCancellationRequested, Is.False);
        var message = new UserChatMessage("loaded", []);
        bodies.SetResult(new Dictionary<Guid, ChatMessage> { [userNode.Id] = message });
        await second;

        await loader.Received(1).LoadMessagesAsync(metadata.Id, userNode.Id, Arg.Any<CancellationToken>());
        Assert.That(data.SelectedTurns.Single(), Is.SameAs(turn));
        Assert.That(data.FindNode(userNode.Id), Is.SameAs(userNode));
        Assert.That(userNode.Message, Is.SameAs(message));
        Assert.That(data.PersistenceDirtyState.HasChanges, Is.False);
    }

    [AvaloniaTheory]
    public void DisposeAsync_WithoutPumpingDispatcher_DrainsAllRootsBeforeFinalSave(bool shouldFailSave)
    {
        var storage = new TestChatContextStorage([]);
        var logger = Substitute.For<ILogger<ChatContextManager>>();
        using var manager = new ChatContextManager(
            new Settings(new ServiceCollection().BuildServiceProvider()), storage, logger);
        using var firstData = new ChatData();
        using var secondData = new ChatData();
        var first = ChatStorageTestExtensions.AttachRoot(manager, firstData);
        var second = ChatStorageTestExtensions.AttachRoot(manager, secondData);
        var nested = first.CreateNested("child");
        var entered = new CountdownEvent(4);
        var completed = 0;
        var operations = new[]
        {
            first.ExecuteAsync(token => RunAsync(first, token)),
            second.ExecuteAsync(token => RunAsync(second, token)),
            nested.ExecuteAsync(token => RunAsync(nested, token)),
            first.RunBackgroundAsync(async token =>
            {
                entered.Signal();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                finally
                {
                    await first.DispatchAsync(() => firstData.Metadata.Topic = "final title");
                    Interlocked.Increment(ref completed);
                }
            })
        };
        var saved = new List<Guid>();
        storage.ContextSaver = data =>
        {
            Assert.That(completed, Is.EqualTo(4));
            Assert.That(first.IsBusy || second.IsBusy || nested.IsBusy, Is.False);
            Assert.That(first.ContextCompaction.IsRunning || second.ContextCompaction.IsRunning || nested.ContextCompaction.IsRunning, Is.False);
            Assert.That(firstData.Metadata.Topic, Is.EqualTo("final title"));
            var user = data.Nodes.Single(node => node.Topology.MessageKind == ChatMessageKind.User);
            var payload = user.Payload ?? throw new AssertionException("Missing user payload.");
            var message = (UserChatMessage)MessagePackSerializer.Deserialize<ChatMessage>(payload);
            Assert.That(message.Content, Is.EqualTo("stopped"));
            saved.Add(data.Metadata.Id);
            return shouldFailSave ? Task.FromException(new IOException("final save failed")) : Task.CompletedTask;
        };

        // Deliberately block the Avalonia thread: runtime cancellation must not ask it to run jobs.
        Assert.That(entered.Wait(TimeSpan.FromSeconds(5)), Is.True);
        Task.Run(async () => await manager.DisposeAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.That(saved, Is.EquivalentTo(new[] { firstData.Metadata.Id, secondData.Metadata.Id }));
        Assert.That(operations.All(task => task.IsCanceled), Is.True);
        Assert.That(firstData.Metadata.States | secondData.Metadata.States, Is.EqualTo(ChatContextMetadataStates.None));
        Assert.That(logger.ReceivedCalls().Any(call => call.GetMethodInfo().Name == "Log" &&
            call.GetArguments()[0] is LogLevel.Error), Is.EqualTo(shouldFailSave));
        Assert.Throws<InvalidOperationException>(() => first.ExecuteAsync(_ => Task.CompletedTask));

        async Task RunAsync(ChatContext context, CancellationToken token)
        {
            await context.DispatchAsync(() => context.Data.Add(new UserChatMessage("running", [])));
            await using var compaction = await context.BeginContextCompactionAsync();
            var interaction = context.UserInterfaceBroker.HandleAskQuestionAsync(
                [new ChatPluginQuestion { Id = "question", Question = "Continue?" }], token);
            entered.Signal();
            try
            {
                await interaction;
            }
            finally
            {
                await context.DispatchAsync(() => ((UserChatMessage)context.Data.Items[^1].Message).Content = "stopped");
                Interlocked.Increment(ref completed);
            }
        }
    }

    [AvaloniaTest]
    public async Task DisposeAsync_WithArchiveAndHistoryInFlight_DrainsStorageWithoutUiPublication()
    {
        var metadata = Metadata("history", 1);
        var storage = new TestChatContextStorage([metadata]);
        using var manager = CreateManager(storage);
        using var target = Data(new UserChatMessage("target", []));
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);


        manager.HistorySearchQuery = "needle";
        manager.HistorySearchIncludesContent = true;
        using var session = manager.BeginLoadSession();
        var historyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var archiveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextLoader = async (_, token) =>
        {
            historyStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return target;
        };
        storage.Archiver = _ =>
        {
            archiveStarted.SetResult();
            return commit.Task;
        };

        var history = Task.Run(async () => await session.LoadMoreAsync(1));
        var archive = manager.ArchiveAsync(target.Metadata);
        await Task.WhenAll(historyStarted.Task, archiveStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        var closing = Task.Run(async () => await manager.DisposeAsync());
        commit.SetResult(DateTimeOffset.UtcNow);
        closing.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        await archive;
        await Assert.ThatAsync(async () => await history, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(target.Metadata.IsArchived, Is.True);
        Assert.That(storage.LoadedContextIds, Has.Count.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Current_WhenReselectedDuringAnotherLoad_CancelsPendingSwitch()
    {
        var other = Metadata("other", 1);
        var storage = new TestChatContextStorage([other]);
        using var manager = CreateManager(storage);
        var current = manager.Current;
        current.Data.Add(new UserChatMessage("current", []));
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextLoader = async (_, token) =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                canceled.SetResult();
            }
            return current.Data;
        };

        manager.CurrentMetadata = other;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.CurrentMetadata = current.Data.Metadata;
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await PumpDispatcherAsync();
        Assert.That(manager.Current, Is.SameAs(current));
        Assert.That(current.Data.IsDisposed, Is.False);
        await manager.FlushPendingSavesAsync();
    }

    [AvaloniaTest]
    public async Task LoadMore_WhenCanceledDuringSecondMatch_RetriesTheWholeUnpublishedPage()
    {
        var first = Metadata("first", 1);
        var second = Metadata("second", 2);
        var storage = new TestChatContextStorage([first, second]);
        using var firstContext = Data(first, new UserChatMessage("needle", []));
        using var secondContext = Data(second, new UserChatMessage("needle", []));
        storage.Contexts[first.Id] = firstContext;
        storage.Contexts[second.Id] = secondContext;
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shouldWait = true;
        storage.ContextLoader = async (id, token) =>
        {
            if (id == second.Id && shouldWait)
            {
                shouldWait = false;
                secondStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return storage.Contexts[id];
        };
        using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "needle";
        manager.HistorySearchIncludesContent = true;
        using var session = manager.BeginLoadSession();
        using var cancellation = new CancellationTokenSource();
        var pending = session.LoadMoreAsync(2, cancellation.Token).AsTask();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThatAsync(async () => await pending, Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Flatten(manager), Is.Empty);

        var result = await session.LoadMoreAsync(2);
        Assert.That(result.AddedItemCount, Is.EqualTo(2));
        Assert.That(Flatten(manager), Is.EqualTo(new[] { first, second }));
    }

    [AvaloniaTest]
    public async Task Archive_WhenAnotherChatCannotSave_SavesOnlyTargetAndRetainsOtherForRetry()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        using var target = Data(new UserChatMessage("target", []));
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);

        using var other = Data(new UserChatMessage("other", []));
        var otherRuntime = ChatStorageTestExtensions.AttachRoot(manager, other);

        var saved = new List<Guid>();
        storage.ContextSaver = context =>
        {
            saved.Add(context.Metadata.Id);
            return context.Metadata.Id == other.Metadata.Id ? Task.FromException(new IOException("other failed")) : Task.CompletedTask;
        };

        await manager.ArchiveAsync(target.Metadata);
        Assert.That(target.Metadata.IsArchived, Is.True);
        Assert.That(saved, Is.EqualTo(new[] { target.Metadata.Id }));
        await Assert.ThatAsync(() => manager.FlushPendingSavesAsync(), Throws.TypeOf<AggregateException>());
        Assert.That(saved.Last(), Is.EqualTo(other.Metadata.Id));
    }

    [AvaloniaTest]
    public async Task Archive_WhenTargetSaveFails_KeepsPendingChangesAndDoesNotPublishArchive()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        using var target = Data(new UserChatMessage("target", []));
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);

        var attempts = 0;
        var archives = 0;
        storage.ContextSaver = _ => ++attempts == 1 ? Task.FromException(new IOException("save failed")) : Task.CompletedTask;
        storage.Archiver = _ =>
        {
            archives++;
            return Task.FromResult(DateTimeOffset.UtcNow);
        };

        await Assert.ThatAsync(() => manager.ArchiveAsync(target.Metadata), Throws.TypeOf<IOException>());
        Assert.That(target.Metadata.IsArchived, Is.False);
        Assert.That(archives, Is.Zero);
        await manager.ArchiveAsync(target.Metadata);
        Assert.That(target.Metadata.IsArchived, Is.True);
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(archives, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task ArchiveThenRestore_WhenArchiveIsPending_PublishesCommittedStatesInOrder()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var metadata = Metadata("archived", 1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource<DateTimeOffset>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hasRestored = false;
        storage.Archiver = _ =>
        {
            started.SetResult();
            return commit.Task;
        };
        storage.Restorer = _ =>
        {
            Assert.That(metadata.IsArchived, Is.True);
            hasRestored = true;
            return Task.CompletedTask;
        };
        var published = new List<DateTimeOffset?>();
        metadata.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatMetadata.ArchivedAt))
            {
                published.Add(metadata.ArchivedAt);
            }
        };

        var archive = manager.ArchiveAsync(metadata);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var restore = manager.RestoreAsync(metadata);
        Assert.That(metadata.IsArchived, Is.False);
        Assert.That(hasRestored, Is.False);
        var timestamp = DateTimeOffset.UtcNow;
        commit.SetResult(timestamp);
        await Task.WhenAll(archive, restore);
        Assert.That(published, Is.EqualTo(new DateTimeOffset?[] { timestamp, null }));
    }

    [AvaloniaTest]
    public async Task Delete_WhenTargetHasUnsavableChanges_DiscardsOnlyTargetWithoutSavingIt()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var user = new UserChatMessage("target", []);
        using var target = Data(user);
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);

        using var other = Data(new UserChatMessage("other", []));
        var otherRuntime = ChatStorageTestExtensions.AttachRoot(manager, other);

        var saved = new List<Guid>();
        storage.ContextSaver = batch =>
        {
            saved.Add(batch.Metadata.Id);
            return Task.FromException(new IOException("cannot serialize"));
        };

        await manager.DeletePermanentlyAsync(target.Metadata);
        Assert.That(saved, Is.Empty);
        user.Content = "late notification";
        await Assert.ThatAsync(() => manager.FlushPendingSavesAsync(), Throws.TypeOf<AggregateException>());
        Assert.That(saved, Is.EqualTo(new[] { other.Metadata.Id }));
    }

    [AvaloniaTheory]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_WhenItFailsOrIsCanceled_RetainsPendingSaveForRetry(bool isCanceled)
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        using var target = Data(new UserChatMessage("target", []));
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);

        target.Metadata.ArchivedAt = DateTimeOffset.UtcNow;
        storage.Deleter = _ => isCanceled
            ? Task.FromCanceled(new CancellationToken(true))
            : Task.FromException(new IOException("delete failed"));
        var saves = 0;
        storage.ContextSaver = _ =>
        {
            saves++;
            return Task.CompletedTask;
        };

        var expectedException = isCanceled ? typeof(OperationCanceledException) : typeof(IOException);
        await Assert.ThatAsync(() => manager.DeletePermanentlyAsync(target.Metadata), Throws.InstanceOf(expectedException));
        Assert.That(saves, Is.Zero);
        await manager.FlushPendingSavesAsync();
        Assert.That(saves, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task Delete_WhenWriteIsInFlight_WaitsWithoutFlushingTheNextRevision()
    {
        var storage = new TestChatContextStorage([]);
        using var manager = CreateManager(storage);
        var user = new UserChatMessage("before", []);
        using var target = Data(user);
        var targetRuntime = ChatStorageTestExtensions.AttachRoot(manager, target);

        target.Metadata.ArchivedAt = DateTimeOffset.UtcNow;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saves = 0;
        storage.ContextSaver = async _ =>
        {
            saves++;
            started.TrySetResult();
            await release.Task;
        };
        var hasDeleted = false;
        storage.Deleter = _ =>
        {
            hasDeleted = true;
            return Task.CompletedTask;
        };

        var writing = manager.FlushPendingSavesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await targetRuntime.DispatchAsync(() => user.Content = "after");
        var deleting = manager.DeletePermanentlyAsync(target.Metadata);
        Assert.That(hasDeleted, Is.False);
        release.SetResult();
        await Task.WhenAll(writing, deleting);
        await manager.FlushPendingSavesAsync();
        Assert.That(saves, Is.EqualTo(1));
        Assert.That(hasDeleted, Is.True);
    }

    [AvaloniaTest]
    public async Task Current_WhenSwitchedOrArchivedWhileRunning_RecomputesBackgroundCount()
    {
        foreach (var action in new[] { "create", "archive", "temporary" })
        {
            var storage = new TestChatContextStorage([]);
            using var manager = CreateManager(storage);
            var current = manager.Current;
            var runtime = current;
            await runtime.DispatchAsync(() => current.Data.Metadata.IsTemporary = action == "temporary");
            await runtime.DispatchAsync(() => current.Data.Add(new UserChatMessage("running", [])));
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var operation = current.ExecuteAsync(_ =>
            {
                started.SetResult();
                return release.Task;
            });
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                // Starting work publishes model state before its UI notification is consumed.
                await PumpDispatcherAsync();
                Assert.That(current.IsBusy, Is.True);
                Assert.That(manager.BackgroundBusyCount, Is.Zero);
                if (action == "archive")
                {
                    await manager.ArchiveAsync(current.Data.Metadata);
                }
                else
                {
                    manager.CreateNewCommand.Execute(null);
                }
                Assert.That(manager.Current, Is.Not.SameAs(current));
                Assert.That(manager.BackgroundBusyCount, Is.EqualTo(1));
            }
            finally
            {
                release.TrySetResult();
                await operation;
                await manager.FlushPendingSavesAsync();
            }
            await PumpDispatcherAsync();
            Assert.That(manager.BackgroundBusyCount, Is.Zero);
        }
    }
}
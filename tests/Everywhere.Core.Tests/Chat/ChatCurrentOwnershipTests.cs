using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.Storage;
using Everywhere.Views;
using MessagePack;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task Current_WhenAcceptedWorkIsCanceledBeforeAdmission_LeavingRetainsTheUserMessage()
    {
        var storage = new TestChatContextStorage([]);
        await using var manager = CreateManager(storage);
        var root = manager.Current;
        // A received commit can temporarily defer accepted work. Cancellation must still
        // enter the send path, which retains the user's message before observing its token.
        var admission = root.TryDeferWorkWhenIdle() ?? throw new AssertionException("Expected an idle root.");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = root.ExecuteAsync(async token =>
        {
            started.SetResult();
            await release.Task;
            await root.DispatchAsync(() => root.Data.Add(new UserChatMessage("accepted message", [])));
            token.ThrowIfCancellationRequested();
        }, cancellation.Token);
        try
        {
            manager.CreateChat(isTemporary: false);
            Assert.That(Flatten(manager), Contains.Item(root.Data.Metadata));
            Assert.That(root.Data.IsDisposed, Is.False);
            cancellation.Cancel();
            admission.SetResult();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            await Assert.ThatAsync(() => work, Throws.InstanceOf<OperationCanceledException>());
            await manager.FlushPendingSavesAsync();
            Assert.That(root.Data.Metadata.IsPersisted, Is.True);
        }
        finally
        {
            admission.TrySetResult();
            release.TrySetResult();
        }
    }

    [AvaloniaTest]
    public async Task Current_WhenStoredChatIsEmpty_LeavingPreservesItsHistoryAndMetadataEdits()
    {
        using var files = ChatTestDatabase.Create();
        using var stored = new ChatData();
        await using (var db = files.Factory.CreateDbContext())
        {
            // Model an existing legacy record, rather than saving a new empty draft.
            db.Chats.Add(ChatMetadataMapper.Capture(stored.Metadata));
            db.Nodes.Add(new ChatV2NodeEntity
            {
                ChatContextId = stored.Metadata.Id, Id = stored.RootNode.Id, MessageKind = ChatMessageKind.Root,
                Payload = MessagePackSerializer.Serialize<ChatMessage>(new RootChatMessage())
            });
            await db.SaveChangesAsync();
        }

        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var history = manager.BeginLoadSession();
        await history.LoadMoreAsync(10);
        var metadata = Flatten(manager).Single();
        var selected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PropertyChangedEventHandler((_, args) =>
        {
            if (args.PropertyName == nameof(ChatContextManager.Current)) selected.TrySetResult();
        });
        manager.PropertyChanged += handler;
        try
        {
            manager.CurrentMetadata = metadata;
            await selected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            manager.PropertyChanged -= handler;
        }

        var root = manager.Current;
        Assert.That(root.Data.HasHistory, Is.False);
        Assert.That(metadata.IsPersisted, Is.True);
        Assert.That(manager.CreateNewCommand.CanExecute(null), Is.True);
        await manager.RenameAsync(metadata, "retained empty history");
        await manager.SetPinnedAsync(metadata, true);
        manager.CreateNewCommand.Execute(null);
        await PumpDispatcherAsync();
        Assert.That(manager.Current, Is.Not.SameAs(root));
        Assert.That(Flatten(manager), Contains.Item(metadata));
        Assert.That(root.Data.IsDisposed, Is.False);
        Assert.That(await manager.LoadChatContextAsync(metadata), Is.SameAs(root));

        var reopened = (await storage.ReadHistoryPageAsync(10)).Items.Single();
        Assert.That(reopened.IsPersisted && reopened.IsPinned, Is.True);
        Assert.That(reopened.Topic, Is.EqualTo("retained empty history"));
    }

    [AvaloniaTest]
    [TestCase(false)]
    [TestCase(true)]
    public async Task Delete_WhenReplacementIsPreparing_PreservesLayoutAndFollowsLatestSelection(bool shouldSelectOther)
    {
        var recent = Metadata("recent", 1);
        var other = Metadata("selected", 2);
        var storage = new TestChatContextStorage([recent, other]);
        storage.Contexts[recent.Id] = Data(recent, new UserChatMessage("recent", []));
        storage.Contexts[other.Id] = Data(other, new UserChatMessage("selected", []));
        await using var manager = CreateManager(storage);
        var previous = manager.Current;
        await previous.ApplyAsync(() => previous.Data.Add(new UserChatMessage("deleted", [])));
        await previous.Presentation.PrepareInitialWindowAsync();
        using var history = manager.BeginLoadSession();
        await history.LoadMoreAsync(10);
        var recentStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextLoader = async (id, token) =>
        {
            (id == recent.Id ? recentStarted : otherStarted).TrySetResult();
            await release.Task.WaitAsync(token);
            return storage.Contexts[id];
        };
        var published = new TaskCompletionSource<RootChatContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new PropertyChangedEventHandler((sender, args) =>
        {
            if (args.PropertyName == nameof(ChatContextManager.Current) && sender is ChatContextManager source)
            {
                published.TrySetResult(source.Current);
            }
        });
        manager.PropertyChanged += handler;
        var items = new ChatMessageItemsControl { Height = 800 };
        items.Bind(ChatMessageItemsControl.ChatContextProperty, new Binding(nameof(ChatContextManager.Current)) { Source = manager });
        var scroll = new ScrollViewer { Content = items };
        var window = new Window { Width = 600, Height = 400, Content = scroll };
        try
        {
            window.Show();
            await manager.DeletePermanentlyAsync(previous.Data.Metadata).WaitAsync(TimeSpan.FromSeconds(5));
            await recentStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (shouldSelectOther)
            {
                manager.CurrentMetadata = other;
                await otherStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            window.UpdateLayout();
            Assert.That(scroll.Viewport.Height, Is.GreaterThan(0));
            Assert.That(items.ChatContext, Is.SameAs(previous));
            Assert.That(previous.Data.IsDisposed, Is.False);
            Assert.That(Flatten(manager), Does.Not.Contain(previous.Data.Metadata));
            window.Width++;
            window.UpdateLayout();

            release.SetResult();
            var current = await published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            window.UpdateLayout();
            Assert.That(current.Data.Metadata.Id, Is.EqualTo(shouldSelectOther ? other.Id : recent.Id));
            Assert.That(items.ChatContext, Is.SameAs(current));
            Assert.That(() => previous.Data.IsDisposed, Is.True.After(5000, 10));
        }
        finally
        {
            release.TrySetResult();
            manager.PropertyChanged -= handler;
            window.Close();
        }
    }
}
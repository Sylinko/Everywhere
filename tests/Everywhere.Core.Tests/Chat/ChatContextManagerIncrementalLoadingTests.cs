using System.Runtime.CompilerServices;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.I18N;
using Everywhere.Storage;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.Chat;

[TestFixture]
public sealed partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task FlushPendingSaves_WhenWriteFails_RetainsContextForExplicitRetry()
    {
        var storage = new TestChatContextStorage([]);
        var attempts = 0;
        storage.ContextSaver = _ => ++attempts == 1 ? Task.FromException(new IOException("disk unavailable")) : Task.CompletedTask;
        using var manager = CreateManager(storage);
        using var data = Data(new UserChatMessage("retained", []));
        var context = ChatStorageTestExtensions.AttachRoot(manager, data);

        data.SelectChild(data.RootNode, null);

        Assert.ThrowsAsync<AggregateException>(() => manager.FlushPendingSavesAsync());
        await manager.FlushPendingSavesAsync();
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(data.HasHistory, Is.True);
        Assert.That(data.Count, Is.EqualTo(1));
    }

    [AvaloniaTest]
    public async Task FlushPendingSaves_WhenEarlierWriteIsRunning_WaitsAndSavesNewChanges()
    {
        var storage = new TestChatContextStorage([]);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        storage.ContextSaver = async _ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                started.SetResult();
                await release.Task;
            }
        };
        using var manager = CreateManager(storage);
        var user = new UserChatMessage("before", []);
        using var data = Data(user);
        var context = ChatStorageTestExtensions.AttachRoot(manager, data);

        var earlier = manager.FlushPendingSavesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await context.DispatchAsync(() => user.Content = "after");
        var final = manager.FlushPendingSavesAsync();
        Assert.That(final.IsCompleted, Is.False);
        release.SetResult();
        await Task.WhenAll(earlier, final);
        Assert.That(attempts, Is.EqualTo(2));
    }

    [AvaloniaTest]
    public async Task ExecuteAsync_WhenOperationCompletes_FlushesFinalStateWithoutAnotherMessage()
    {
        var storage = new TestChatContextStorage([]);
        var saved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextSaver = _ =>
        {
            saved.TrySetResult();
            return Task.CompletedTask;
        };
        using var manager = CreateManager(storage);
        using var data = new ChatData();
        var context = ChatStorageTestExtensions.AttachRoot(manager, data);
        await context.ExecuteAsync(_ =>
        {
            data.Add(new UserChatMessage("completed", []));
            return Task.CompletedTask;
        });
        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(context.IsBusy, Is.False);
    }

    [AvaloniaTest]
    public async Task LoadMoreAsync_WithTitleFilter_ReturnsRequestedFinalMatchCount()
    {
        var storage = new TestChatContextStorage(
        [
            Metadata("skip one", 1),
            Metadata("match one", 2),
            Metadata("skip two", 3),
            Metadata("match two", 4),
            Metadata("match three", 5)
        ]);
        using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "match";

        using var session = manager.BeginLoadSession();
        var result = await session.LoadMoreAsync(2);
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(2));
            Assert.That(result.HasMoreItems, Is.True);
            Assert.That(
                Flatten(manager).Select(metadata => metadata.Topic),
                Is.EqualTo(new[] { "match one", "match two" }));
        });
    }

    [AvaloniaTest]
    public async Task LoadMoreAsync_WithContentSearch_SkipsToolContentAndStopsAfterPageIsFull()
    {
        var toolOnly = Metadata("tool", 1);
        var userMatch = Metadata("user", 2);
        var laterMatch = Metadata("later", 3);
        var storage = new TestChatContextStorage([toolOnly, userMatch, laterMatch]);
        storage.Contexts[toolOnly.Id] = Data(
            toolOnly,
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });
        storage.Contexts[userMatch.Id] = Data(userMatch, new UserChatMessage("contains needle", []));
        storage.Contexts[laterMatch.Id] = Data(laterMatch, new UserChatMessage("also contains needle", []));
        using var manager = CreateManager(storage);
        manager.HistorySearchIncludesContent = true;
        manager.HistorySearchQuery = "needle";

        using var session = manager.BeginLoadSession();
        var result = await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(1));
            Assert.That(Flatten(manager), Is.EqualTo(new[] { userMatch }));
            Assert.That(storage.LoadedContextIds, Is.EqualTo(new[] { toolOnly.Id, userMatch.Id }));
        });
    }

    [AvaloniaTest]
    public async Task HistorySearchQuery_WhenChanged_ClearsResultsAndRetargetsSameSession()
    {
        var alpha = Metadata("alpha", 1);
        var beta = Metadata("beta", 2);
        var storage = new TestChatContextStorage([alpha, beta]);
        using var manager = CreateManager(storage);
        manager.HistorySearchQuery = "alpha";
        using var session = manager.BeginLoadSession();

        await session.LoadMoreAsync(1);
        manager.HistorySearchQuery = "beta";
        await PumpDispatcherAsync();
        Assert.That(Flatten(manager), Is.Empty);

        await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        Assert.That(Flatten(manager), Is.EqualTo(new[] { beta }));
    }

    [AvaloniaTest]
    public async Task HistorySearchQuery_WhenChangedDuringLoad_RetargetsInFlightRequest()
    {
        var first = Metadata("unrelated one", 1);
        var second = Metadata("unrelated two", 2);
        var storage = new TestChatContextStorage([first, second]);
        storage.Contexts[first.Id] = Data(first, new UserChatMessage("alpha", []));
        storage.Contexts[second.Id] = Data(second, new UserChatMessage("beta", []));
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstAttempt = 0;
        storage.ContextLoader = async (id, cancellationToken) =>
        {
            if (id == first.Id && Interlocked.Increment(ref firstAttempt) == 1)
            {
                firstLoadStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return storage.Contexts[id];
        };
        using var manager = CreateManager(storage);
        manager.HistorySearchIncludesContent = true;
        manager.HistorySearchQuery = "alpha";
        using var session = manager.BeginLoadSession();

        var load = session.LoadMoreAsync(1).AsTask();
        await firstLoadStarted.Task;
        manager.HistorySearchQuery = "beta";
        var result = await load;
        await PumpDispatcherAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.AddedItemCount, Is.EqualTo(1));
            Assert.That(Flatten(manager), Is.EqualTo(new[] { second }));
        });
    }

    [AvaloniaTest]
    public async Task LoadSession_AcrossMultipleRequests_KeepsManagerBusyUntilDisposed()
    {
        var storage = new TestChatContextStorage([Metadata("one", 1), Metadata("two", 2)]);
        using var manager = CreateManager(storage);
        var session = manager.BeginLoadSession();

        Assert.That(manager.IsBusy, Is.True);
        await session.LoadMoreAsync(1);
        await session.LoadMoreAsync(1);
        Assert.That(manager.IsBusy, Is.True);

        session.Dispose();
        Assert.That(manager.IsBusy, Is.False);
    }

    [AvaloniaTest]
    public async Task CurrentMetadata_WhenHistoryIsLong_PublishesPrewarmedTailWindowOnUiThread()
    {
        var metadata = Metadata("long history", 1);
        var messages = new List<ChatMessage>();
        for (var index = 0; index < ChatPresentation.TurnBatchSize + 3; index++)
        {
            messages.Add(new UserChatMessage($"Question {index}", []));
            messages.Add(Assistant($"Answer {index}"));
        }

        var storage = new TestChatContextStorage([metadata]);
        storage.Contexts[metadata.Id] = Data(metadata, [.. messages]);
        using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        var currentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedOnUiThread = false;
        var publishedWithPreparedTail = false;
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChatContextManager.Current))
            {
                return;
            }

            publishedOnUiThread = Dispatcher.UIThread.CheckAccess();
            var rows = manager.Current.Presentation.Rows;
            var publishedOutputs = rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
            publishedWithPreparedTail = rows.OfType<ChatMessagePresentationRow>().Count() == ChatPresentation.TurnBatchSize &&
                publishedOutputs.Length == ChatPresentation.TurnBatchSize &&
                publishedOutputs.All(row => row.Markdown.DocumentUpdate?.Version == row.TextSpan.ContentMarkdownBuilder.Version);
            currentChanged.TrySetResult();
        };

        manager.CurrentMetadata = metadata;
        await currentChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var presentation = manager.Current.Presentation;
        var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(publishedOnUiThread, Is.True);
            Assert.That(publishedWithPreparedTail, Is.True);
            Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
            Assert.That(((UserChatMessage)users[0].Node.Message).Content, Is.EqualTo("Question 3"));
            Assert.That(presentation.HasEarlierTurns, Is.True);
            Assert.That(presentation.HasLaterTurns, Is.False);
        });
    }

    [AvaloniaTest]
    public async Task CurrentMetadata_WhenEarlierLoadFinishesLast_KeepsLatestSelection()
    {
        var first = Metadata("first", 1);
        var second = Metadata("second", 2);
        var storage = new TestChatContextStorage([first, second]);
        storage.Contexts[first.Id] = Data(first, new UserChatMessage("First", []), Assistant("First"));
        storage.Contexts[second.Id] = Data(second, new UserChatMessage("Second", []), Assistant("Second"));
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstLoadCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextLoader = async (id, cancellationToken) =>
        {
            if (id == first.Id)
            {
                firstLoadStarted.TrySetResult();
                try
                {
                    await releaseFirstLoad.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    firstLoadCanceled.TrySetResult();
                    throw;
                }
            }

            return storage.Contexts[id];
        };
        using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(2);
        await PumpDispatcherAsync();

        var latestPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatContextManager.Current) &&
                ReferenceEquals(manager.Current.Data, storage.Contexts[second.Id]))
            {
                latestPublished.TrySetResult();
            }
        };

        manager.CurrentMetadata = first;
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.CurrentMetadata = second;
        await firstLoadCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await latestPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirstLoad.TrySetResult();
        await PumpDispatcherAsync();

        Assert.That(manager.Current.Data, Is.SameAs(storage.Contexts[second.Id]));
        Assert.That(storage.Contexts[first.Id].IsDisposed, Is.False, "A stale UI switch does not own the canonical model lifetime.");
    }

    [Test]
    public async Task Contains_WhenOnlyToolContentMatches_ReturnsFalse()
    {
        using var data = Data(
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });
        await using var context = new RootChatContext(data, null);
        Assert.That(
            await ChatTextSearcher.ContainsAsync(
                context,
                new TextSearchPattern("needle"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.False);
    }

    [Test]
    public async Task Contains_WhenAssistantTextIsSplitByMarkdownFormatting_MatchesVisualText()
    {
        using var data = Data(Assistant("Hel**lo**"));
        await using var context = new RootChatContext(data, null);
        Assert.That(
            await ChatTextSearcher.ContainsAsync(
                context,
                new TextSearchPattern("Hello"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.True);
    }

    [Test]
    public async Task Contains_WhenOnlyMarkdownLinkDestinationMatches_ReturnsFalse()
    {
        using var data = Data(Assistant("[visible](https://hidden.example)"));
        await using var context = new RootChatContext(data, null);
        Assert.That(
            await ChatTextSearcher.ContainsAsync(
                context,
                new TextSearchPattern("hidden.example"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.False);
    }

    private static ChatContextManager CreateManager(IChatContextStorage storage) =>
        new(
            new Settings(new ServiceCollection().BuildServiceProvider()),
            storage,
            NullLogger<ChatContextManager>.Instance);

    private static ChatMetadata[] Flatten(ChatContextManager manager) =>
        manager.AllHistory.SelectMany(group => group.MetadataList).ToArray();

    private static ChatMetadata Metadata(string topic, int minutesAgo)
    {
        var modified = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        return new ChatMetadata(Guid.CreateVersion7(), modified, modified, topic);
    }

    private static ChatData Data(params ChatMessage[] messages)
    {
        var data = new ChatData();
        foreach (var message in messages) data.Add(message);
        return data;
    }

    private static ChatData Data(ChatMetadata metadata, params ChatMessage[] messages)
    {
        var modified = metadata.DateModified;
        var root = new ChatMessageNode(Guid.Empty, new RootChatMessage());
        var data = new ChatData(metadata, [], root);
        foreach (var message in messages)
        {
            data.Add(message);
        }

        metadata.DateModified = modified;
        // These fixtures represent database-loaded conversations, not newly unsaved graphs.
        data.PersistenceDirtyState.Acknowledge(data.PersistenceDirtyState.Capture());
        return data;
    }

    private static AssistantChatMessage Assistant(string markdown)
    {
        var message = new AssistantChatMessage();
        message.AddSpan(new AssistantChatMessageTextSpan(markdown));
        return message;
    }

    private static async Task PumpDispatcherAsync()
    {
        await Task.Yield();
        await Dispatcher.UIThread.InvokeAsync(static () => { });
    }

    private sealed class TestChatContextStorage(IReadOnlyList<ChatMetadata> metadata) : IChatContextStorage
    {
        public Dictionary<Guid, ChatData> Contexts { get; } = [];

        public List<Guid> LoadedContextIds { get; } = [];

        public Func<Guid, CancellationToken, Task<ChatData>>? ContextLoader { get; set; }

        public Func<ChatWriteBatch, Task>? ContextSaver { get; set; }

        public Func<Guid, Task<DateTimeOffset>>? Archiver { get; set; }

        public Func<Guid, Task>? Restorer { get; set; }

        public Func<Guid, Task>? Deleter { get; set; }

        public Task<DateTimeOffset> ArchiveAsync(Guid chatId, CancellationToken cancellationToken = default)
        {
            return Archiver?.Invoke(chatId) ?? Task.FromResult(DateTimeOffset.UtcNow);
        }

        public Task RestoreAsync(Guid chatId, CancellationToken cancellationToken = default)
        {
            return Restorer?.Invoke(chatId) ?? Task.CompletedTask;
        }

        public Task DeletePermanentlyAsync(Guid chatId, CancellationToken cancellationToken = default)
        {
            return Deleter?.Invoke(chatId) ?? Task.CompletedTask;
        }

        public async IAsyncEnumerable<ChatMetadata> QueryChatContextsAsync(
            int take,
            Guid? startAfterId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default,
            bool isArchived = false)
        {
            var startIndex = startAfterId is { } cursor ? metadata.Select(item => item.Id).ToList().IndexOf(cursor) + 1 : 0;
            foreach (var item in metadata.Skip(startIndex).Where(item => item.IsArchived == isArchived).Take(take))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task<ChatData> GetChatContextAsync(
            ChatMetadata metadata,
            CancellationToken cancellationToken = default)
        {
            var chatContextId = metadata.Id;
            cancellationToken.ThrowIfCancellationRequested();
            LoadedContextIds.Add(chatContextId);
            return ContextLoader?.Invoke(chatContextId, cancellationToken) ??
                Task.FromResult(Contexts[chatContextId]);
        }

        public Task SaveChatContextAsync(
            ChatWriteBatch data,
            CancellationToken cancellationToken = default) => ContextSaver?.Invoke(data) ?? Task.CompletedTask;

        public Task SaveChatContextMetadataAsync(
            ChatMetadataWrite metadata,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<Guid, ChatMessage>> LoadMessagesAsync(Guid chatId, Guid? turnId, CancellationToken cancellationToken)
        {
            throw new AssertionException("These fixtures supply fully loaded conversations.");
        }

        public Task<string> ReadUserTextAsync(Guid chatId, Guid nodeId, ChatMessageKind kind, CancellationToken cancellationToken)
        {
            throw new AssertionException("These fixtures supply fully loaded conversations.");
        }
    }

}
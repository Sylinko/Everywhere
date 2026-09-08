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
public sealed class ChatContextManagerIncrementalLoadingTests
{
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
            Assert.That(Flatten(manager).Select(metadata => metadata.Topic),
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
        storage.Contexts[toolOnly.Id] = Context(
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });
        storage.Contexts[userMatch.Id] = Context(new UserChatMessage("contains needle", []));
        storage.Contexts[laterMatch.Id] = Context(new UserChatMessage("also contains needle", []));
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
        storage.Contexts[first.Id] = Context(new UserChatMessage("alpha", []));
        storage.Contexts[second.Id] = Context(new UserChatMessage("beta", []));
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
    public async Task CurrentMetadata_WhenHistoryLoads_PublishesPrewarmedMarkdownOnUiThread()
    {
        var metadata = Metadata("history", 1);
        var storage = new TestChatContextStorage([metadata]);
        var assistant = Assistant("# Loaded\n\nHistory");
        storage.Contexts[metadata.Id] = Context(new UserChatMessage("Question", []), assistant);
        using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        var currentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedOnUiThread = false;
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ChatContextManager.Current)) return;

            publishedOnUiThread = Dispatcher.UIThread.CheckAccess();
            currentChanged.TrySetResult();
        };

        manager.CurrentMetadata = metadata;
        await currentChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var row = manager.Current.Presentation.Rows.OfType<AssistantTextOutputPresentationRow>().Single();
        var builder = row.TextSpan.ContentMarkdownBuilder;
        Assert.Multiple(() =>
        {
            Assert.That(publishedOnUiThread, Is.True);
            Assert.That(row.CachedDocumentUpdate?.Version, Is.EqualTo(builder.Version));
            Assert.That(row.RenderingMarkdownBuilder, Is.Null);
        });
    }

    [AvaloniaTest]
    public async Task CurrentMetadata_WhenHistoryIsLong_PublishesOnlyPrewarmedTailWindow()
    {
        var metadata = Metadata("long history", 1);
        var messages = new List<ChatMessage>();
        for (var index = 0; index < ChatPresentation.TurnBatchSize + 3; index++)
        {
            messages.Add(new UserChatMessage($"Question {index}", []));
            messages.Add(Assistant($"Answer {index}"));
        }

        var storage = new TestChatContextStorage([metadata]);
        storage.Contexts[metadata.Id] = Context([.. messages]);
        using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(1);
        await PumpDispatcherAsync();

        var currentChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        manager.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatContextManager.Current)) currentChanged.TrySetResult();
        };

        manager.CurrentMetadata = metadata;
        await currentChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var presentation = manager.Current.Presentation;
        var users = presentation.Rows.OfType<ChatMessagePresentationRow>().ToArray();
        var outputs = presentation.Rows.OfType<AssistantTextOutputPresentationRow>().ToArray();
        Assert.Multiple(() =>
        {
            Assert.That(users, Has.Length.EqualTo(ChatPresentation.TurnBatchSize));
            Assert.That(((UserChatMessage)users[0].Node.Message).Content, Is.EqualTo("Question 3"));
            Assert.That(presentation.HasEarlierTurns, Is.True);
            Assert.That(outputs, Has.All.Matches<AssistantTextOutputPresentationRow>(row =>
                row.CachedDocumentUpdate?.Version == row.TextSpan.ContentMarkdownBuilder.Version));
        });
    }

    [AvaloniaTest]
    public async Task CurrentMetadata_WhenEarlierLoadFinishesLast_KeepsLatestSelection()
    {
        var first = Metadata("first", 1);
        var second = Metadata("second", 2);
        var storage = new TestChatContextStorage([first, second]);
        storage.Contexts[first.Id] = Context(new UserChatMessage("First", []), Assistant("First"));
        storage.Contexts[second.Id] = Context(new UserChatMessage("Second", []), Assistant("Second"));
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextLoader = async (id, cancellationToken) =>
        {
            if (id == first.Id)
            {
                firstLoadStarted.TrySetResult();
                await releaseFirstLoad.Task.WaitAsync(cancellationToken);
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
                ReferenceEquals(manager.Current, storage.Contexts[second.Id]))
            {
                latestPublished.TrySetResult();
            }
        };

        manager.CurrentMetadata = first;
        await firstLoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        manager.CurrentMetadata = second;
        await latestPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseFirstLoad.TrySetResult();
        await PumpDispatcherAsync();

        Assert.That(manager.Current, Is.SameAs(storage.Contexts[second.Id]));
    }

    [Test]
    public void Contains_WhenOnlyToolContentMatches_ReturnsFalse()
    {
        using var context = Context(
            new FunctionCallChatMessage(LucideIconKind.Hammer, new DirectLocaleKey("Tool"))
            {
                Content = "needle"
            });

        Assert.That(
            ChatTextSearcher.Contains(
                context,
                new TextSearchPattern("needle"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.False);
    }

    [Test]
    public void Contains_WhenAssistantTextIsSplitByMarkdownFormatting_MatchesVisualText()
    {
        using var context = Context(Assistant("Hel**lo**"));

        Assert.That(
            ChatTextSearcher.Contains(
                context,
                new TextSearchPattern("Hello"),
                new MarkdownTextProjector(),
                CancellationToken.None),
            Is.True);
    }

    [Test]
    public void Contains_WhenOnlyMarkdownLinkDestinationMatches_ReturnsFalse()
    {
        using var context = Context(Assistant("[visible](https://hidden.example)"));

        Assert.That(
            ChatTextSearcher.Contains(
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

    private static ChatContextMetadata[] Flatten(ChatContextManager manager) =>
        manager.AllHistory.SelectMany(group => group.MetadataList).ToArray();

    private static ChatContextMetadata Metadata(string topic, int minutesAgo)
    {
        var modified = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo);
        return new ChatContextMetadata(Guid.CreateVersion7(), modified, modified, topic);
    }

    private static ChatContext Context(params ChatMessage[] messages)
    {
        var context = new ChatContext();
        foreach (var message in messages) context.Add(message);
        return context;
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

    private sealed class TestChatContextStorage(IReadOnlyList<ChatContextMetadata> metadata) : IChatContextStorage
    {
        public Dictionary<Guid, ChatContext> Contexts { get; } = [];

        public List<Guid> LoadedContextIds { get; } = [];

        public Func<Guid, CancellationToken, Task<ChatContext>>? ContextLoader { get; set; }

        public Task DeleteChatContextsAsync(
            IEnumerable<Guid> chatContextIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RestoreChatContextsAsync(
            IEnumerable<Guid> chatContextIds,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async IAsyncEnumerable<ChatContextMetadata> QueryChatContextsAsync(
            int take,
            ChatContextOrderBy orderBy,
            bool descending,
            Guid? startAfterId = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var startIndex = startAfterId is { } cursor
                ? metadata.Select(item => item.Id).ToList().IndexOf(cursor) + 1
                : 0;
            foreach (var item in metadata.Skip(startIndex).Take(take))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
                await Task.Yield();
            }
        }

        public Task<ChatContext> GetChatContextAsync(
            Guid chatContextId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedContextIds.Add(chatContextId);
            return ContextLoader?.Invoke(chatContextId, cancellationToken) ??
                   Task.FromResult(Contexts[chatContextId]);
        }

        public Task SaveChatContextAsync(
            ChatContext context,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveChatContextMetadataAsync(
            ChatContextMetadata metadata,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}

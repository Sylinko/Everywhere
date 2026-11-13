using Avalonia.Headless.NUnit;
using DynamicData;
using DynamicData.Binding;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Statistics;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using Everywhere.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

namespace Everywhere.Core.Tests.Chat;

public sealed partial class ChatContextManagerIncrementalLoadingTests
{
    // A selection creates its own chat. It must not consume the unsent composer's text,
    // clear its singleton storage slot, or make New Chat abandon that input.
    [AvaloniaTest]
    public async Task NewDraft_WhenSelectionStartsAnotherChat_ReturnsToSameInputAndHandsOffOnSend()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var service = Substitute.For<IChatService>();
        var submitted = Task.CompletedTask;
        bool Accept(ChatContext context, UserChatMessage message)
        {
            submitted = context.ExecuteAsync(_ => context.DispatchAsync(() => context.Data.Add(message)));
            return true;
        }
        service.SendMessage(Arg.Any<ChatContext>(), Arg.Any<UserChatMessage>())
            .Returns(call => Accept(call.ArgAt<ChatContext>(0), call.ArgAt<UserChatMessage>(1)));
        service.SendMessage(Arg.Any<ChatContext>(), Arg.Any<UserChatMessage>(), Arg.Any<CustomAssistant?>())
            .Returns(call => Accept(call.ArgAt<ChatContext>(0), call.ArgAt<UserChatMessage>(1)));
        var initialStrategy = new Strategy { Id = "default", NameKey = new DirectLocaleKey("Default") };
        var attachmentStrategy = new Strategy { Id = "attachment", NameKey = new DirectLocaleKey("With attachment") };
        var strategies = Substitute.For<IStrategyEngine>();
        strategies.GetStrategies(Arg.Any<StrategyContext>()).Returns(call =>
            call.Arg<StrategyContext>().Attachments.Count == 0 ? new[] { initialStrategy } : new[] { attachmentStrategy });
        var composer = CreateComposer(manager, service, lifetime, strategies);
        // Regression: empty attachment lists must initialize strategy cards on both chats.
        await ExpectStrategyAsync(composer, initialStrategy);
        var original = manager.Current;
        var draft = original.Input.ActiveDraft;
        composer.ChatInputAreaText = "unfinished question";
        var selection = new TextSelectionAttachment("selected text");
        var unrelated = new TextAttachment(new DirectLocaleKey("note"), "keep this attachment");
        draft.AttachmentsSource.AddRange([selection, unrelated]);
        await ExpectStrategyAsync(composer, attachmentStrategy);
        await manager.FlushPendingSavesAsync();
        Assert.That((await storage.ReadNewChatInputAsync(CancellationToken.None))?.ChatId, Is.EqualTo(original.Data.Metadata.Id));

        Assert.That(composer.SendTextSelectionMessage(new UserChatMessage("selection question", [selection]), null), Is.True);
        await submitted;
        await manager.FlushPendingSavesAsync();
        var independent = manager.Current;
        Assert.That(independent, Is.Not.SameAs(original));
        Assert.That(composer.ChatInputAreaText, Is.Null.Or.Empty);
        await ExpectStrategyAsync(composer, initialStrategy);
        var retained = await storage.ReadNewChatInputAsync(CancellationToken.None);
        Assert.That(retained?.Snapshot.Draft.Text, Is.EqualTo("unfinished question"));
        Assert.That(retained?.ChatId, Is.EqualTo(original.Data.Metadata.Id));
        Assert.That(draft.AttachmentsSource.Items, Is.EqualTo(new[] { unrelated }));

        manager.CreateNewCommand.Execute(null);
        Assert.That(manager.Current, Is.SameAs(original));
        Assert.That(original.Input.ActiveDraft, Is.SameAs(draft));
        Assert.That(composer.ChatInputAreaText, Is.EqualTo("unfinished question"));
        await ExpectStrategyAsync(composer, attachmentStrategy);
        manager.CreateNewCommand.Execute(null);
        Assert.That(manager.Current, Is.SameAs(original));

        await composer.SendMessageCommand.ExecuteAsync(composer.ChatInputAreaText);
        await submitted;
        await manager.FlushPendingSavesAsync();
        Assert.That(await storage.ReadNewChatInputAsync(CancellationToken.None), Is.Null);
        Assert.That(original.Data.Metadata.IsPersisted, Is.True);
        Assert.That(composer.ChatInputAreaText, Is.Null.Or.Empty);

        static async Task ExpectStrategyAsync(ChatWindowViewModel composer, Strategy expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await composer.WhenValueChanged(model => model.StrategiesSnapshot)
                .Where(snapshot => snapshot is { Count: 1 } && ReferenceEquals(snapshot[0], expected))
                .Take(1)
                .ToTask(timeout.Token);
        }
    }

    [AvaloniaTest]
    public async Task Input_WhenRestarted_RestoresNewChatAndExistingEditWithoutChangingHistory()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        ChatMetadata metadata;
        Guid nodeId;
        Guid newChatId;
        DateTimeOffset modified;
        await using (var manager = CreateManager(storage))
        {
            var existing = manager.Current;
            var node = await existing.DispatchAsync(() => existing.Data.Add(new UserChatMessage("original question", [])));
            metadata = existing.Data.Metadata;
            nodeId = node.Id;
            modified = metadata.DateModified;
            await manager.FlushPendingSavesAsync();
            existing.Input.Draft.Text = "ordinary draft";
            existing.Input.BeginEdit(node);
            existing.Input.ActiveDraft.Text = "partially edited question";
            manager.CreateNewCommand.Execute(null);
            newChatId = manager.Current.Data.Metadata.Id;
            manager.Current.Input.Draft.Text = "new unsent question";
        }

        await using var restored = CreateManager(storage);
        await restored.InitializeAsync(CancellationToken.None);
        Assert.That(restored.Current.Data.Metadata.Id, Is.EqualTo(newChatId));
        Assert.That(restored.Current.Input.Draft.Text, Is.EqualTo("new unsent question"));
        Assert.That(restored.Current.Data.HasHistory, Is.False);
        var existingContext = await restored.LoadChatContextAsync(metadata) ?? throw new AssertionException("Missing saved chat.");
        Assert.That(existingContext.Input.Edit?.NodeId, Is.EqualTo(nodeId));
        Assert.That(existingContext.Input.ActiveDraft.Text, Is.EqualTo("partially edited question"));
        existingContext.Input.EndEdit();
        Assert.That(existingContext.Input.ActiveDraft.Text, Is.EqualTo("ordinary draft"));
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
        Assert.That((await db.Chats.AsNoTracking().SingleAsync()).UpdatedAt, Is.EqualTo(modified));
        Assert.That(await db.SyncPending.CountAsync(), Is.Zero);
    }

    private static ChatWindowViewModel CreateComposer(
        ChatContextManager manager,
        IChatService service,
        System.Reactive.Disposables.CompositeDisposable lifetime,
        IStrategyEngine strategies)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        lifetime.Add(services);
        var settings = new Settings(services);
        settings.TextSelection.ShouldStartToolbarChatsAsTemporary = false;
        strategies.Registry.Returns(new StrategyRegistry([]));
        var actions = new TextSelectionToolbarActionsManager(settings, strategies);
        lifetime.Add(actions);
        var toolbar = new TextSelectionToolbarService(services, settings, Substitute.For<IOverlayInputWatcher>(),
            Substitute.For<IWindowHelper>(), actions, NullLogger<TextSelectionToolbarService>.Instance);
        lifetime.Add(toolbar);
        var visual = new ChatVisualService(new UnusedHostConnectionSource());
        lifetime.Add(visual);
        var composer = new ChatWindowViewModel(settings, new PersistentState(new InMemoryKeyValueStorage()), manager,
            Substitute.For<ISoftwareUpdater>(), service, Substitute.For<IScreenSelectionService>(), Substitute.For<IBlobStorage>(),
            strategies, Substitute.For<IGreetings>(), visual, toolbar, NullLogger<ChatWindowViewModel>.Instance);
        lifetime.Add(composer);
        return composer;
    }

    private sealed class UnusedHostConnectionSource : IHostConnectionSource
    {
        public ValueTask<RpcConnection> GetConnectionAsync(ProcessRole role, CancellationToken cancellationToken = default)
        {
            throw new AssertionException("Text-only composer operations must not require a native host.");
        }

        public IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(ProcessRole role, CancellationToken cancellationToken = default)
        {
            throw new AssertionException("Text-only composer operations must not require a native host.");
        }
    }
}
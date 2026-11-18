using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using DynamicData;
using DynamicData.Binding;
using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Automation;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Harness;
using Everywhere.I18N;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Skills;
using Everywhere.Statistics;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.TextSelection;
using Everywhere.ViewModels;
using MessagePack;
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
        service.SendMessageAsync(Arg.Any<ChatContext>(), Arg.Any<UserChatMessage>(), Arg.Any<CustomAssistant?>())
            .Returns(call =>
            {
                var context = call.ArgAt<ChatContext>(0);
                return context.TryStartAsync(
                    () => context.Data.StartTurn(call.ArgAt<UserChatMessage>(1)),
                    (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                    IExceptionHandler.DangerouslyIgnoreAllException);
            });
        service.EditAsync(Arg.Any<ChatContext>(), Arg.Any<ChatMessageNode>(), Arg.Any<UserChatMessage>())
            .Returns(call =>
            {
                var context = call.ArgAt<ChatContext>(0);
                return context.TryStartAsync(
                    () => context.Data.CreateBranchOn(call.ArgAt<ChatMessageNode>(1), call.ArgAt<UserChatMessage>(2)),
                    (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
                    IExceptionHandler.DangerouslyIgnoreAllException);
            });
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

        Assert.That(await composer.SendTextSelectionMessageAsync(new UserChatMessage("selection question", [selection]), null), Is.True);
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

        var admission = original.TryDeferWorkWhenIdle() ?? throw new AssertionException("The draft must be idle before submission.");
        Task sending;
        try
        {
            sending = composer.SendMessageCommand.ExecuteAsync(composer.ChatInputAreaText);
            Assert.That(sending.IsCompleted, Is.False);
            Assert.That(draft.Text, Is.EqualTo("unfinished question"));
            Assert.That(composer.RemoveAttachmentCommand.CanExecute(unrelated), Is.False);
            await manager.FlushPendingSavesAsync();
            Assert.That((await storage.ReadNewChatInputAsync(CancellationToken.None))?.Snapshot.Draft.Text, Is.EqualTo("unfinished question"));
            await SelectAsync(manager, independent);
            independent.Input.Draft.Text = "another chat's input";
        }
        finally
        {
            admission.SetResult();
        }
        await sending.WaitAsync(TimeSpan.FromSeconds(5));
        await manager.FlushPendingSavesAsync();
        Assert.That(await storage.ReadNewChatInputAsync(CancellationToken.None), Is.Null);
        Assert.That(original.Data.Metadata.IsPersisted, Is.True);
        Assert.That(original.IsBusy, Is.True, "Publication must not wait for generation to finish.");
        Assert.That(composer.ChatInputAreaText, Is.EqualTo("another chat's input"));
        await SelectAsync(manager, original);
        Assert.That(composer.ChatInputAreaText, Is.Null.Or.Empty);

        // A continued edit must retain later input and follow the published branch. Canceling
        // before admission opens must still retain the accepted user message.
        await original.StopAsync();
        original.ResumeAfterStop();
        original.Input.Draft.Text = "ordinary input";
        composer.EditMessageNodeCommand.Execute(original.Data.SelectedNodes[^1]);
        var editingDraft = original.Input.ActiveDraft;
        editingDraft.Text = "edited question";
        var editAdmission = original.TryDeferWorkWhenIdle() ?? throw new AssertionException("The edit must start idle.");
        Task editing;
        try
        {
            editing = composer.SendMessageCommand.ExecuteAsync(editingDraft.Text);
            editingDraft.Text = "edited question with later input";
            composer.CancelEditing();
            Assert.That(original.Input.ActiveDraft, Is.SameAs(editingDraft));
            original.Cancel();
        }
        finally
        {
            editAdmission.SetResult();
        }
        await editing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(original.Input.ActiveDraft, Is.SameAs(editingDraft));
        Assert.That(editingDraft.Text, Is.EqualTo("edited question with later input"));
        Assert.That(original.Input.Edit?.NodeId, Is.EqualTo(original.Data.SelectedNodes[^1].Id));
        Assert.That(original.Input.Draft.Text, Is.EqualTo("ordinary input"));
        composer.CancelEditing();

        // An import started on A must stay with A even when navigation occurs during hashing,
        // including every file in the same drag/drop batch.
        var path = Path.Combine(Path.GetTempPath(), $"chat-import-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "imported text");
            var importing = composer.AddFilesFromDragDropAsync([path, path], CancellationToken.None);
            manager.CreateNewCommand.Execute(null);
            await importing;
            Assert.That(original.Input.Draft.AttachmentsSource.Items.OfType<FileAttachment>().Count(), Is.EqualTo(2));
            Assert.That(manager.Current.Input.ActiveDraft.AttachmentsSource.Count, Is.Zero);
        }
        finally
        {
            File.Delete(path);
        }

        static async Task SelectAsync(ChatContextManager manager, RootChatContext context)
        {
            manager.CurrentMetadata = context.Data.Metadata;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await manager.WhenValueChanged(owner => owner.Current)
                .Where(current => ReferenceEquals(current, context)).Take(1).ToTask(timeout.Token);
        }

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
    public async Task Input_WhenRestarted_RestoresExistingEditAndSavesDraftOnFirstSend()
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
            var node = await existing.DispatchAsync(() => existing.Data.StartTurn(new UserChatMessage("original question", [])));
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
        var draft = restored.Current;
        var text = draft.Input.Draft.Text ?? throw new AssertionException("Missing restored draft text.");
        Assert.That(draft.Data.Metadata.Id, Is.EqualTo(newChatId));
        Assert.That(text, Is.EqualTo("new unsent question"));
        Assert.That(draft.Data.HasHistory, Is.False);
        var existingContext = await restored.LoadChatContextAsync(metadata) ?? throw new AssertionException("Missing saved chat.");
        Assert.That(existingContext.Input.Edit?.NodeId, Is.EqualTo(nodeId));
        Assert.That(existingContext.Input.ActiveDraft.Text, Is.EqualTo("partially edited question"));
        existingContext.Input.EndEdit();
        Assert.That(existingContext.Input.ActiveDraft.Text, Is.EqualTo("ordinary draft"));
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.Chats.CountAsync(), Is.EqualTo(1));
        Assert.That((await db.Chats.AsNoTracking().SingleAsync()).UpdatedAt, Is.EqualTo(modified));
        Assert.That(await db.SyncPending.CountAsync(), Is.Zero);

        // The restored input has a chat ID, but its first message still needs a new graph root.
        await draft.DispatchAsync(() => draft.Data.StartTurn(new UserChatMessage(text, [])));
        await restored.FlushPendingSavesAsync();

        var saved = await storage.GetChatContextAsync(draft.Data.Metadata, CancellationToken.None);
        await using var reopened = new RootChatContext(saved, storage);
        var turn = saved.SelectedTurns.Single();
        await reopened.EnsureTurnLoadedAsync(turn, CancellationToken.None);
        Assert.That(((UserChatMessage)turn.UserNode.Message).Content, Is.EqualTo(text));
    }

    [AvaloniaTest]
    public async Task PendingInputs_WhenRestarted_RestoresPausedQueueAndPreservesLegacyThreeElementSnapshot()
    {
        var legacyBytes = MessagePackSerializer.Serialize((
            new ChatInputDraftSnapshot("legacy", [], null),
            (Guid?)null,
            (ChatInputDraftSnapshot?)null));
        var legacyRestored = MessagePackSerializer.Deserialize<ChatInputSnapshot>(legacyBytes);
        Assert.Multiple(() =>
        {
            Assert.That(legacyRestored.Draft.Text, Is.EqualTo("legacy"));
            Assert.That(legacyRestored.PendingInputs, Is.Null);
        });

        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        ChatMetadata metadata;
        Guid firstPendingId;
        await using (var manager = CreateManager(storage))
        {
            var existing = manager.Current;
            await existing.DispatchAsync(() => existing.Data.StartTurn(new UserChatMessage("initial", [])));
            metadata = existing.Data.Metadata;
            await manager.FlushPendingSavesAsync();

            var fileAttachment = new FileAttachment(new DirectLocaleKey("note.txt"), @"C:\temp\note.txt", "deadbeef", "text/plain");
            var first = existing.PendingInputs.Enqueue(
                new UserChatMessage("queued follow-up", [fileAttachment]),
                ChatInputDeliveryMode.Queue,
                null);
            firstPendingId = first.Id;
            existing.PendingInputs.Enqueue(
                new UserChatMessage("steer hint", []),
                ChatInputDeliveryMode.Steer,
                null);
            Dispatcher.UIThread.RunJobs();
            await manager.FlushPendingSavesAsync();
        }

        await using var restored = CreateManager(storage);
        await restored.InitializeAsync(CancellationToken.None);
        var reopened = await restored.LoadChatContextAsync(metadata) ?? throw new AssertionException("Missing saved chat.");
        Assert.Multiple(() =>
        {
            Assert.That(reopened.PendingInputs.IsDrainPaused, Is.True);
            Assert.That(reopened.PendingInputs.WaitingItems.Count, Is.EqualTo(2));
            Assert.That(reopened.PendingInputs.WaitingItems[0].Id, Is.EqualTo(firstPendingId));
            Assert.That(reopened.PendingInputs.WaitingItems[0].DeliveryMode, Is.EqualTo(ChatInputDeliveryMode.Queue));
            Assert.That(reopened.PendingInputs.WaitingItems[0].PreviewText, Is.EqualTo("queued follow-up"));
            Assert.That(
                reopened.PendingInputs.WaitingItems[0].Message.Attachments.OfType<FileAttachment>().Single().FilePath,
                Is.EqualTo(@"C:\temp\note.txt"));
            Assert.That(reopened.PendingInputs.WaitingItems[1].DeliveryMode, Is.EqualTo(ChatInputDeliveryMode.Steer));
            Assert.That(reopened.PendingInputs.WaitingItems[1].PreviewText, Is.EqualTo("steer hint"));
        });

        reopened.PendingInputs.ClearAllUserItems();
        Dispatcher.UIThread.RunJobs();
        await restored.FlushPendingSavesAsync();
        await using var db = files.Factory.CreateDbContext();
        Assert.That(await db.ChatInputs.CountAsync(), Is.Zero);
    }

    [AvaloniaTest]
    public async Task ComposerPrimaryAction_WhenIdleWithPausedQueueOrInterruptedTurn_SwitchesBetweenResumeAndSend()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var services = new ServiceCollection().BuildServiceProvider();
        lifetime.Add(services);
        var settings = new Settings(services);
        var visual = new ChatVisualService(new UnusedHostConnectionSource());
        lifetime.Add(visual);
        var realChatService = new ChatService(
            manager,
            Substitute.For<IChatPluginManager>(),
            Substitute.For<IModelSessionFactory>(),
            new AssistantSpecializationResolver(
                new AssistantCatalog(Substitute.For<IPresetModelProvider>(), Substitute.For<IOfficialModelProvider>())),
            Substitute.For<IBlobStorage>(),
            settings,
            Substitute.For<IPromptService>(),
            Substitute.For<ISkillPromptProvider>(),
            Substitute.For<IStatisticsRecorder>(),
            visual,
            Substitute.For<IVisualContextScanEffect>(),
            services,
            NullLogger<ChatService>.Instance);

        var service = Substitute.For<IChatService>();
        service.CanResume(Arg.Any<ChatContext>()).Returns(call => realChatService.CanResume(call.ArgAt<ChatContext>(0)));
        var strategies = Substitute.For<IStrategyEngine>();
        var composer = CreateComposer(manager, service, lifetime, strategies);

        Assert.Multiple(() =>
        {
            Assert.That(composer.CanResumeConversation, Is.False);
            Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Send));
            Assert.That(composer.IsPrimaryButtonVisible, Is.False);
            Assert.That(composer.IsPrimaryButtonEnabled, Is.False);
        });

        await using var completedContext = new RootChatContext(new ChatData(), null);
        completedContext.Data.StartTurn(new UserChatMessage("Done question", []));
        var completedAssistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        completedAssistant.AddSpan(new AssistantChatMessageTextSpan("done") { FinishedAt = DateTimeOffset.UtcNow });
        completedContext.Data.Append(completedAssistant);
        Assert.That(realChatService.CanResume(completedContext), Is.False);

        await using var interruptedContext = new RootChatContext(new ChatData(), null);
        interruptedContext.Data.StartTurn(new UserChatMessage("Canceled question", []));
        interruptedContext.Data.Append(new AssistantChatMessage { IsCanceled = true });
        Assert.That(realChatService.CanResume(interruptedContext), Is.True);

        await using var unansweredContext = new RootChatContext(new ChatData(), null);
        unansweredContext.Data.StartTurn(new UserChatMessage("Unanswered question", []));
        Assert.That(realChatService.CanResume(unansweredContext), Is.True);

        manager.Current.PendingInputs.Restore(
            [new ChatPendingInputSnapshot(
                Guid.NewGuid(),
                new ChatInputDraftSnapshot("paused", [], null),
                ChatInputDeliveryMode.Queue,
                null,
                DateTimeOffset.UtcNow)],
            _ => null,
            pauseDrain: true);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(composer.CanResumeConversation, Is.True);
            Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Resume));
            Assert.That(composer.IsPrimaryButtonVisible, Is.True);
            Assert.That(composer.IsPrimaryButtonEnabled, Is.True);
        });

        composer.ChatInputAreaText = "typing new message";
        Assert.Multiple(() =>
        {
            Assert.That(composer.CanResumeConversation, Is.False);
            Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Send));
            Assert.That(composer.CanSendDraft, Is.True);
        });

        composer.ChatInputAreaText = string.Empty;
        Assert.That(composer.CanResumeConversation, Is.True);
        Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Resume));
        composer.PrimaryComposerActionCommand.Execute(null);
        service.Received(1).Resume(manager.Current);
    }

    [AvaloniaTest]
    public async Task PrimaryComposerMode_AndTurnInactiveCommands_TrackBusyAndSubmissionMode()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var service = Substitute.For<IChatService>();
        var strategies = Substitute.For<IStrategyEngine>();
        var composer = CreateComposer(manager, service, lifetime, strategies);

        manager.Current.Data.StartTurn(new UserChatMessage("hello", []));
        var assistant = new AssistantChatMessage { IsBusy = false, FinishedAt = DateTimeOffset.UtcNow };
        assistant.AddSpan(new AssistantChatMessageTextSpan("reply") { FinishedAt = DateTimeOffset.UtcNow });
        var assistantNode = manager.Current.Data.Append(assistant);

        Assert.Multiple(() =>
        {
            Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Send));
            Assert.That(composer.IsTurnInactive, Is.True);
            Assert.That(composer.RetryMessageNodeCommand.CanExecute(assistantNode), Is.True);
            Assert.That(composer.ContinueMessageNodeCommand.CanExecute(assistantNode), Is.True);
            Assert.That(composer.CompactContextCommand.CanExecute(null), Is.True);
        });

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Assert.That(
            manager.Current.TryExecute(_ => release.Task, Substitute.For<IExceptionHandler>()),
            Is.True);
        Dispatcher.UIThread.RunJobs();

        composer.Settings.ChatWindow.BusySubmissionMode = ChatInputDeliveryMode.Queue;
        Assert.Multiple(() =>
        {
            Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Queue));
            Assert.That(composer.IsTurnInactive, Is.False);
            Assert.That(composer.RetryMessageNodeCommand.CanExecute(assistantNode), Is.False);
            Assert.That(composer.ContinueMessageNodeCommand.CanExecute(assistantNode), Is.False);
            Assert.That(composer.CompactContextCommand.CanExecute(null), Is.False);
        });

        composer.Settings.ChatWindow.BusySubmissionMode = ChatInputDeliveryMode.Steer;
        Assert.That(composer.PrimaryComposerMode, Is.EqualTo(ChatComposerPrimaryMode.Steer));

        release.SetResult();
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
            strategies, visual, toolbar, NullLogger<ChatWindowViewModel>.Instance);
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
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Everywhere.Chat;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Core.Tests.Statistics;
using Everywhere.Database;
using Everywhere.I18N;
using Everywhere.Storage;
using Everywhere.StrategyEngine;
using Everywhere.Views;
using NSubstitute;

namespace Everywhere.Core.Tests.Chat;

partial class ChatContextManagerIncrementalLoadingTests
{
    [AvaloniaTest]
    public async Task RefreshHistory_WhenBackgroundChatSaveIsBlocked_KeepsItVisibleAndDeduplicatesAfterCommit()
    {
        using var files = ChatTestDatabase.Create();
        var innerStorage = new ChatTurnStorage(files.Factory);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new InterceptingChatContextStorage(innerStorage)
        {
            OnSaveChatContextAsync = async (batch, save) =>
            {
                saveStarted.TrySetResult();
                await releaseSave.Task;
                await save(batch);
            }
        };

        await using var manager = CreateManager(storage);
        var chatA = manager.Current;
        await chatA.DispatchAsync(() =>
        {
            chatA.Data.StartTurn(new UserChatMessage("first message in A", []));
            chatA.Data.Metadata.Rename("Chat A");
        });

        var saving = manager.FlushPendingSavesAsync();
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(chatA.Data.Metadata.IsPersisted, Is.False);

            manager.CreateNewCommand.Execute(null);
            var chatB = manager.Current;
            Assert.That(chatB, Is.Not.SameAs(chatA));

            // Simulate a full history reload (such as switching views) while A's first commit is still blocked.
            manager.HistoryView = ChatHistoryView.Archived;
            manager.HistoryView = ChatHistoryView.Recent;

            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.HasMoreItems, Is.False);
                Assert.That(manager.Current, Is.SameAs(chatB));
                Assert.That(
                    Flatten(manager).Select(metadata => metadata.Id),
                    Is.EqualTo([chatA.Data.Metadata.Id]));
                Assert.That(
                    Flatten(manager).Select(metadata => metadata.Id),
                    Does.Not.Contain(chatB.Data.Metadata.Id));
            }

            releaseSave.SetResult();
            await saving;
            await PumpDispatcherAsync();

            var visibleIds = Flatten(manager).Select(metadata => metadata.Id).ToArray();
            var persistedPage = await innerStorage.ReadHistoryPageAsync(20);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.Current, Is.SameAs(chatB));
                Assert.That(chatA.Data.Metadata.IsPersisted, Is.True);
                Assert.That(visibleIds.Count(id => id == chatA.Data.Metadata.Id), Is.EqualTo(1));
                Assert.That(visibleIds, Is.EqualTo([chatA.Data.Metadata.Id]));
                Assert.That(visibleIds, Does.Not.Contain(chatB.Data.Metadata.Id));
                Assert.That(persistedPage.Items.Select(item => item.Id), Does.Contain(chatA.Data.Metadata.Id));
            }
        }
        finally
        {
            releaseSave.TrySetResult();
        }
    }

    [AvaloniaTest]
    public async Task LoadMoreHistory_WhenChatCommitsMidScan_RetainsAndDeduplicatesBothInterleavings()
    {
        // Interleaving 1: A commits after local candidates are captured and before ReadHistoryPageAsync runs,
        // so both the local overlay and the database page contain A.
        using (var files = ChatTestDatabase.Create())
        {
            var innerStorage = new ChatTurnStorage(files.Factory);
            var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var storage = new InterceptingChatContextStorage(innerStorage);

            await using var manager = CreateManager(storage);
            var chatA = manager.Current;
            await chatA.DispatchAsync(() =>
            {
                chatA.Data.StartTurn(new UserChatMessage("interleaved A", []));
                chatA.Data.Metadata.Rename("Chat A");
            });

            manager.CreateNewCommand.Execute(null);
            var chatB = manager.Current;

            manager.HistoryView = ChatHistoryView.Archived;
            manager.HistoryView = ChatHistoryView.Recent;

            var pageCount = 0;
            storage.OnReadHistoryPageAsync = async read =>
            {
                if (Interlocked.Increment(ref pageCount) == 1)
                {
                    readStarted.TrySetResult();
                    await releaseRead.Task;
                }

                return await read();
            };

            using var session = manager.BeginLoadSession();
            var loading = session.LoadMoreAsync(20).AsTask();
            try
            {
                await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await manager.FlushPendingSavesAsync();
                Assert.That(chatA.Data.Metadata.IsPersisted, Is.True);
                releaseRead.SetResult();
                await loading;
                await PumpDispatcherAsync();

                var visibleIds = Flatten(manager).Select(metadata => metadata.Id).ToArray();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(manager.Current, Is.SameAs(chatB));
                    Assert.That(visibleIds.Count(id => id == chatA.Data.Metadata.Id), Is.EqualTo(1));
                    Assert.That(visibleIds, Is.EqualTo([chatA.Data.Metadata.Id]));
                    Assert.That(visibleIds, Does.Not.Contain(chatB.Data.Metadata.Id));
                }
            }
            finally
            {
                releaseRead.TrySetResult();
            }
        }

        // Interleaving 2: ReadHistoryPageAsync page 1 completes without A2 (no matches on page 1),
        // then A2 commits (leaving _pendingSaves) before page 2 completes and the same generation publishes.
        using (var files = ChatTestDatabase.Create())
        {
            var innerStorage = new ChatTurnStorage(files.Factory);
            for (var index = 0; index < 105; index++)
            {
                var oldChat = Data(new UserChatMessage($"old {index}", []));
                oldChat.Metadata.Rename(index == 104 ? "match old" : $"skip {index}");
                oldChat.Metadata.DateModified = DateTimeOffset.UtcNow.AddMinutes(-10 - index);
                await innerStorage.SaveDataAsync(oldChat);
            }

            var pageCount = 0;
            var secondPageStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecondPage = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var storage = new InterceptingChatContextStorage(innerStorage);

            await using var manager2 = CreateManager(storage);
            var chatA2 = manager2.Current;
            await chatA2.DispatchAsync(() =>
            {
                chatA2.Data.StartTurn(new UserChatMessage("message in A2", []));
                chatA2.Data.Metadata.Rename("match A2");
            });

            var chatB2 = manager2.CreateChat(isTemporary: false);
            manager2.HistorySearchQuery = "match";
            storage.OnReadHistoryPageAsync = async read =>
            {
                if (Interlocked.Increment(ref pageCount) == 2)
                {
                    secondPageStarted.TrySetResult();
                    await releaseSecondPage.Task;
                }

                return await read();
            };

            using var session2 = manager2.BeginLoadSession();
            // Request 2 matches: page 1 fetches 100 "skip" rows (0 matches), forcing page 2 in the same generation.
            var loading2 = session2.LoadMoreAsync(2).AsTask();
            try
            {
                await secondPageStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await manager2.FlushPendingSavesAsync();
                Assert.That(chatA2.Data.Metadata.IsPersisted, Is.True);
                releaseSecondPage.SetResult();
                await loading2;
                await PumpDispatcherAsync();

                var visibleIds = Flatten(manager2).Select(metadata => metadata.Id).ToArray();
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(manager2.Current, Is.SameAs(chatB2));
                    Assert.That(visibleIds.Count(id => id == chatA2.Data.Metadata.Id), Is.EqualTo(1));
                    Assert.That(visibleIds, Does.Contain(chatA2.Data.Metadata.Id));
                    Assert.That(visibleIds, Does.Not.Contain(chatB2.Data.Metadata.Id));
                }
            }
            finally
            {
                releaseSecondPage.TrySetResult();
            }
        }
    }

    [AvaloniaTest]
    public async Task LoadMoreHistory_WhenHistoryExceedsOnePageWithColdPins_PlacesColdPinsFirstAndUncommittedChatOnFirstPage()
    {
        var pinnedOld = Metadata("pinned old", 500);
        pinnedOld.IsPinned = true;
        var pinnedOlder = Metadata("pinned older", 600);
        pinnedOlder.IsPinned = true;
        var coldRows = Enumerable.Range(1, 105).Select(index => Metadata($"cold {index}", index + 10)).ToArray();
        var storage = new TestChatContextStorage([pinnedOld, pinnedOlder, .. coldRows]);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.ContextSaver = async _ =>
        {
            saveStarted.TrySetResult();
            await releaseSave.Task;
        };

        await using var manager = CreateManager(storage);
        // Keep an abandoned empty _newChatDraft in _roots/_metadataMap while switching to another chat.
        var abandonedDraft = manager.Current;
        var chatA = manager.CreateChat(isTemporary: false);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chatA, Is.Not.SameAs(abandonedDraft));
            Assert.That(abandonedDraft.Data.HasHistory, Is.False);
        }

        await chatA.DispatchAsync(() =>
        {
            chatA.Data.StartTurn(new UserChatMessage("uncommitted A", []));
            chatA.Data.Metadata.Rename("Uncommitted A");
        });
        var saving = manager.FlushPendingSavesAsync();
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var chatB = manager.CreateChat(isTemporary: false);

            manager.HistoryView = ChatHistoryView.Archived;
            manager.HistoryView = ChatHistoryView.Recent;

            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(10);
            await PumpDispatcherAsync();

            var visible = Flatten(manager);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.HasMoreItems, Is.True);
                Assert.That(visible, Does.Not.Contain(abandonedDraft.Data.Metadata));
                Assert.That(visible, Does.Not.Contain(chatB.Data.Metadata));
                Assert.That(visible.Take(2), Is.EqualTo([pinnedOld, pinnedOlder]));
                Assert.That(visible, Does.Contain(chatA.Data.Metadata));
            }
        }
        finally
        {
            releaseSave.TrySetResult();
            await saving;
        }
    }

    [AvaloniaTest]
    public async Task HistoryMaterialization_WhenCommittedNewChatSurvivesSearchClearAndUnrelatedDelete_DoesNotDisappear()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        var oldChat = Data(new UserChatMessage("old message", []));
        oldChat.Metadata.Rename("Old Chat");
        await storage.SaveDataAsync(oldChat);
        var oldMetadata = oldChat.Metadata;

        await using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(20);
        await PumpDispatcherAsync();
        Assert.That(manager.HasMoreItems, Is.False);

        var chatA = manager.Current;
        await chatA.DispatchAsync(() =>
        {
            chatA.Data.StartTurn(new UserChatMessage("alpha body", []));
            chatA.Data.Metadata.Rename("Alpha Target");
        });
        await manager.FlushPendingSavesAsync();
        Assert.That(chatA.Data.Metadata.IsPersisted, Is.True);

        manager.CreateNewCommand.Execute(null);
        var chatB = manager.Current;
        await chatB.DispatchAsync(() =>
        {
            chatB.Data.StartTurn(new UserChatMessage("beta body", []));
            chatB.Data.Metadata.Rename("Beta Current");
        });
        await manager.FlushPendingSavesAsync();

        // Searching and then clearing the search query must still find and retain committed chat A.
        manager.HistorySearchQuery = "Alpha";
        await session.LoadMoreAsync(20);
        await PumpDispatcherAsync();
        Assert.That(Flatten(manager).Select(metadata => metadata.Id), Is.EqualTo([chatA.Data.Metadata.Id]));

        manager.HistorySearchQuery = null;
        await session.LoadMoreAsync(20);
        await PumpDispatcherAsync();
        Assert.That(
            Flatten(manager).Select(metadata => metadata.Id),
            Is.EquivalentTo([chatB.Data.Metadata.Id, chatA.Data.Metadata.Id, oldMetadata.Id]));

        // Deleting an unrelated conversation must keep both chat A and chat B materialized.
        var trackedOld = Flatten(manager).Single(metadata => metadata.Id == oldMetadata.Id);
        await manager.DeletePermanentlyAsync(trackedOld);
        await PumpDispatcherAsync();
        await session.LoadMoreAsync(20);
        await PumpDispatcherAsync();

        Assert.That(
            Flatten(manager).Select(metadata => metadata.Id),
            Is.EquivalentTo([chatB.Data.Metadata.Id, chatA.Data.Metadata.Id]));
    }

    [AvaloniaTest]
    public async Task TemporaryAndAcceptedWorkChats_WhenUncommittedBeforeSave_AppearInMatchingHistoryViews()
    {
        using var files = ChatTestDatabase.Create();
        var innerStorage = new ChatTurnStorage(files.Factory);
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var storage = new InterceptingChatContextStorage(innerStorage)
        {
            OnSaveChatContextAsync = async (batch, save) =>
            {
                saveStarted.TrySetResult();
                await releaseSave.Task;
                await save(batch);
            }
        };

        await using var manager = CreateManager(storage);
        var tempA = manager.CreateChat(isTemporary: true);
        await tempA.DispatchAsync(() =>
        {
            tempA.Data.StartTurn(new UserChatMessage("temp message", []));
            tempA.Data.Metadata.Rename("Temp A");
        });

        var saving = manager.FlushPendingSavesAsync();
        TaskCompletionSource? admission = null;
        Task? pendingWork = null;
        try
        {
            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var regularB = manager.CreateChat(isTemporary: false);
            manager.HistoryView = ChatHistoryView.Temporary;
            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Flatten(manager), Is.EqualTo([tempA.Data.Metadata]));
                Assert.That(Flatten(manager), Does.Not.Contain(regularB.Data.Metadata));
            }

            releaseSave.SetResult();
            await saving;
            await PumpDispatcherAsync();
            Assert.That(Flatten(manager).Count(metadata => metadata.Id == tempA.Data.Metadata.Id), Is.EqualTo(1));

            // Accepted work before its first turn is saved must also survive a history restart.
            var workC = manager.CreateChat(isTemporary: false);
            admission = workC.TryDeferWorkWhenIdle() ?? throw new AssertionException("Expected an idle root.");
            pendingWork = workC.ExecuteAsync(_ => Task.CompletedTask, CancellationToken.None);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(workC.HasWork, Is.True);
                Assert.That(workC.Data.HasHistory, Is.False);
            }

            manager.CreateChat(isTemporary: false);
            manager.HistoryView = ChatHistoryView.Recent;
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();

            Assert.That(Flatten(manager), Does.Contain(workC.Data.Metadata));
        }
        finally
        {
            releaseSave.TrySetResult();
            admission?.TrySetResult();
            if (pendingWork is not null)
            {
                await pendingWork;
            }
        }
    }

    [AvaloniaTest]
    public async Task DeleteOrArchiveSelectedChat_WhenRemainingHistoryExists_ClearsSelectionAndSwitchesToNewChat()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        var oldest = Data(new UserChatMessage("oldest message", []));
        oldest.Metadata.Rename("Oldest");
        oldest.Metadata.DateModified = DateTimeOffset.UtcNow.AddMinutes(-10);
        await storage.SaveDataAsync(oldest);

        var middle = Data(new UserChatMessage("middle message", []));
        middle.Metadata.Rename("Middle");
        middle.Metadata.DateModified = DateTimeOffset.UtcNow.AddMinutes(-5);
        await storage.SaveDataAsync(middle);

        var newest = Data(new UserChatMessage("newest message", []));
        newest.Metadata.Rename("Newest");
        newest.Metadata.DateModified = DateTimeOffset.UtcNow.AddMinutes(-1);
        await storage.SaveDataAsync(newest);

        await using var manager = CreateManager(storage);
        using var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var composer = CreateComposer(manager, Substitute.For<IChatService>(), lifetime, Substitute.For<IStrategyEngine>());
        var emptyDraft = manager.Current;
        var listBox = new ChatHistoryListBox { ContextManager = manager };
        var window = new Window { Width = 400, Height = 400, Content = listBox };
        try
        {
            window.Show();
            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();

            var trackedNewest = Flatten(manager).Single(metadata => metadata.Id == newest.Metadata.Id);
            var trackedMiddle = Flatten(manager).Single(metadata => metadata.Id == middle.Metadata.Id);
            var trackedOldest = Flatten(manager).Single(metadata => metadata.Id == oldest.Metadata.Id);

            await SwitchToMetadataAsync(manager, trackedNewest);
            await PumpDispatcherAsync();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.Current.Data.Metadata.Id, Is.EqualTo(trackedNewest.Id));
                Assert.That(listBox.SelectedItem, Is.SameAs(trackedNewest));
                Assert.That(Flatten(manager), Does.Not.Contain(emptyDraft.Data.Metadata));
            }

            var replacementPublished = WaitForNextCurrentAsync(manager);
            await manager.DeletePermanentlyAsync(trackedNewest);
            await replacementPublished;
            await PumpDispatcherAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Flatten(manager), Is.EqualTo([trackedMiddle, trackedOldest]));
                Assert.That(listBox.SelectedItem, Is.Null);
                Assert.That(manager.Current.Data.HasHistory, Is.False);
                Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Metadata_Topic_New.I18N()));
            }

            await SwitchToMetadataAsync(manager, trackedMiddle);
            await PumpDispatcherAsync();
            Assert.That(listBox.SelectedItem, Is.SameAs(trackedMiddle));

            var archivedReplacement = WaitForNextCurrentAsync(manager);
            await manager.ArchiveAsync(trackedMiddle);
            await archivedReplacement;
            await PumpDispatcherAsync();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Flatten(manager), Is.EqualTo([trackedOldest]));
                Assert.That(listBox.SelectedItem, Is.Null);
                Assert.That(manager.Current.Data.HasHistory, Is.False);
                Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Metadata_Topic_New.I18N()));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task DeleteSelectedChat_WhenDeletingLastConversation_LeavesHistoryEmptyUntilFirstMessageStarts()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        var onlyChat = Data(new UserChatMessage("only message", []));
        onlyChat.Metadata.Rename("Only Chat");
        onlyChat.Metadata.DateModified = DateTimeOffset.UtcNow.AddMinutes(-2);
        await storage.SaveDataAsync(onlyChat);

        await using var manager = CreateManager(storage);
        _ = manager.Current;
        var listBox = new ChatHistoryListBox { ContextManager = manager };
        var window = new Window { Width = 400, Height = 400, Content = listBox };
        var workGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            window.Show();
            using var session = manager.BeginLoadSession();
            await session.LoadMoreAsync(20);
            await PumpDispatcherAsync();

            var trackedOnly = Flatten(manager).Single(metadata => metadata.Id == onlyChat.Metadata.Id);
            await SwitchToMetadataAsync(manager, trackedOnly);
            await PumpDispatcherAsync();
            Assert.That(listBox.SelectedItem, Is.SameAs(trackedOnly));

            var fallbackPublished = WaitForNextCurrentAsync(manager);
            await manager.DeletePermanentlyAsync(trackedOnly);
            await fallbackPublished;
            await PumpDispatcherAsync();

            var newChat = manager.Current;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(newChat.Data.Metadata.Id, Is.Not.EqualTo(trackedOnly.Id));
                Assert.That(Flatten(manager), Is.Empty);
                Assert.That(listBox.SelectedItem, Is.Null);
            }

            var started = await newChat.TryStartAsync(
                () =>
                {
                    newChat.Data.StartTurn(new UserChatMessage("after delete", []));
                    newChat.Data.Metadata.Rename("After Delete");
                    return true;
                },
                (_, token) => workGate.Task.WaitAsync(token),
                IExceptionHandler.DangerouslyIgnoreAllException);
            Assert.That(started, Is.True);
            await PumpDispatcherAsync();
            // Background AutoRefresh notifications from TryStartAsync schedule via DispatcherPriority.Background.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                static () => { },
                Avalonia.Threading.DispatcherPriority.Background);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(Flatten(manager), Is.EqualTo([newChat.Data.Metadata]));
                Assert.That(listBox.SelectedItem, Is.SameAs(newChat.Data.Metadata));
            }

            var secondChat = manager.CreateChat(isTemporary: false);
            await PumpDispatcherAsync();
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
                static () => { },
                Avalonia.Threading.DispatcherPriority.Background);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.Current, Is.SameAs(secondChat));
                Assert.That(manager.BackgroundBusyCount, Is.EqualTo(1));
                Assert.That(Flatten(manager), Is.EqualTo([newChat.Data.Metadata]));
                Assert.That(listBox.SelectedItem, Is.Null);
            }
        }
        finally
        {
            workGate.TrySetResult();
            window.Close();
        }
    }

    [AvaloniaTest]
    public async Task CurrentTitleKey_WhenTransitioningFromDraftToActiveChat_SwitchesBetweenNewAndDefaultTitles()
    {
        using var files = ChatTestDatabase.Create();
        var storage = new ChatTurnStorage(files.Factory);
        await using var manager = CreateManager(storage);
        using var lifetime = new System.Reactive.Disposables.CompositeDisposable();
        var service = Substitute.For<IChatService>();
        var strategies = Substitute.For<IStrategyEngine>();
        using var composer = CreateComposer(manager, service, lifetime, strategies);

        var draft = manager.Current;
        Assert.That(draft.Data.Metadata.IsTemporary, Is.False);
        Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Metadata_Topic_New.I18N()));

        await draft.ApplyAsync(() => draft.Data.Metadata.SetTemporary(true));
        await PumpDispatcherAsync();
        Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Temporary_New.I18N()));

        var releaseWork = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var started = await draft.TryStartAsync(
                () =>
                {
                    draft.Data.StartTurn(new UserChatMessage("image only", []));
                    return true;
                },
                (_, token) => releaseWork.Task.WaitAsync(token),
                IExceptionHandler.DangerouslyIgnoreAllException);
            Assert.That(started, Is.True);
            await PumpDispatcherAsync();
            Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Temporary.I18N()));

            await manager.PersistChatAsync(draft.Data.Metadata);
            await PumpDispatcherAsync();
            Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Metadata_Topic_Default.I18N()));

            await manager.RenameAsync(draft.Data.Metadata, "Custom Topic");
            await PumpDispatcherAsync();
            Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo("Custom Topic"));

            await manager.RenameAsync(draft.Data.Metadata, "");
            await PumpDispatcherAsync();
            Assert.That(composer.CurrentTitleKey.ToString(), Is.EqualTo(Core.I18N.LocaleKey.ChatContext_Metadata_Topic_Default.I18N()));
        }
        finally
        {
            releaseWork.TrySetResult();
        }
    }

    [AvaloniaTest]
    public async Task CommitReceivedSnapshot_WhenHistoryAlreadyExhausted_PublishesSyncedRecentChatImmediately()
    {
        var existing = Metadata("Existing Chat", 5);
        var innerStorage = new TestChatContextStorage([existing]);
        var syncedId = Guid.CreateVersion7();
        var syncedModified = DateTimeOffset.UtcNow;
        var syncedRow = new ChatV2Entity
        {
            Id = syncedId,
            Topic = "Synced Chat",
            CreatedAt = syncedModified,
            UpdatedAt = syncedModified
        };
        var storage = new InterceptingChatContextStorage(innerStorage)
        {
            OnMergeSnapshotAsync = () => Task.FromResult<ReceivedChatCommit?>(
                new ReceivedChatCommit(syncedRow, [], []))
        };

        await using var manager = CreateManager(storage);
        using var session = manager.BeginLoadSession();
        await session.LoadMoreAsync(100);
        await PumpDispatcherAsync();
        Assert.That(manager.HasMoreItems, Is.False);
        Assert.That(Flatten(manager).Select(item => item.Id), Is.EqualTo([existing.Id]));

        var head = new ChatSyncHead(syncedId.ToString("D"), 1, ChatSyncLifecycle.Active, 1, ChatSyncAvailability.Available, 0, 1);
        using var snapshot = Activator.CreateInstance(
                typeof(ChatSyncDownloadedSnapshot),
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
                null,
                [head, 0L],
                null) as ChatSyncDownloadedSnapshot
            ?? throw new AssertionException("Missing ChatSyncDownloadedSnapshot constructor.");

        var committed = await manager.CommitReceivedSnapshotAsync("account-1", snapshot, false, CancellationToken.None);
        await PumpDispatcherAsync();
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(
            static () => { },
            Avalonia.Threading.DispatcherPriority.Background);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(committed, Is.True);
            Assert.That(Flatten(manager).Select(item => item.Id), Is.EqualTo([syncedId, existing.Id]));
        }
    }

    private static async Task SwitchToMetadataAsync(ChatContextManager manager, ChatMetadata target)
    {
        var switched = WaitForNextCurrentAsync(manager, target.Id);
        manager.CurrentMetadata = target;
        await switched;
    }

    private static async Task WaitForNextCurrentAsync(ChatContextManager manager, Guid? expectedId = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ChatContextManager.Current) &&
                (expectedId is null || manager.Current.Data.Metadata.Id == expectedId.Value))
            {
                completion.TrySetResult();
            }
        }

        manager.PropertyChanged += Handler;
        try
        {
            if (expectedId is not null && manager.Current.Data.Metadata.Id == expectedId.Value)
            {
                return;
            }

            await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            manager.PropertyChanged -= Handler;
        }
    }

    private sealed class InterceptingChatContextStorage(IChatContextStorage inner) : IChatContextStorage
    {
        public Func<ChatWriteBatch, Func<ChatWriteBatch, Task>, Task>? OnSaveChatContextAsync { get; init; }

        public Func<Func<Task<ChatHistoryPage>>, Task<ChatHistoryPage>>? OnReadHistoryPageAsync { get; set; }

        public Func<Task<ReceivedChatCommit?>>? OnMergeSnapshotAsync { get; init; }

        public Task<ChatHistoryPage> ReadHistoryPageAsync(
            int take,
            ChatHistoryCursor? cursor = null,
            ChatHistoryView view = ChatHistoryView.Recent,
            CancellationToken cancellationToken = default)
        {
            Task<ChatHistoryPage> Read() => inner.ReadHistoryPageAsync(take, cursor, view, cancellationToken);
            return OnReadHistoryPageAsync is not null ? OnReadHistoryPageAsync(Read) : Read();
        }

        public Task<ChatData> GetChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default) =>
            inner.GetChatContextAsync(metadata, cancellationToken);

        public Task<IReadOnlyDictionary<Guid, ChatMessage>> LoadMessagesAsync(
            Guid chatId,
            Guid? turnId,
            CancellationToken cancellationToken) =>
            inner.LoadMessagesAsync(chatId, turnId, cancellationToken);

        public Task<string> ReadUserTextAsync(Guid chatId, Guid nodeId, ChatMessageKind kind, CancellationToken cancellationToken) =>
            inner.ReadUserTextAsync(chatId, nodeId, kind, cancellationToken);

        public Task SaveChatContextAsync(ChatWriteBatch batch, CancellationToken cancellationToken = default)
        {
            Task Save(ChatWriteBatch target) => inner.SaveChatContextAsync(target, cancellationToken);
            return OnSaveChatContextAsync is not null ? OnSaveChatContextAsync(batch, Save) : Save(batch);
        }

        public Task SaveChatContextMetadataAsync(ChatMetadataWrite metadata, CancellationToken cancellationToken = default) =>
            inner.SaveChatContextMetadataAsync(metadata, cancellationToken);

        public Task<DateTimeOffset> ArchiveAsync(Guid chatId, CancellationToken cancellationToken = default) =>
            inner.ArchiveAsync(chatId, cancellationToken);

        public Task RestoreAsync(Guid chatId, CancellationToken cancellationToken = default) =>
            inner.RestoreAsync(chatId, cancellationToken);

        public Task SetPinnedAsync(Guid chatId, bool isPinned, CancellationToken cancellationToken = default) =>
            inner.SetPinnedAsync(chatId, isPinned, cancellationToken);

        public Task DeletePermanentlyAsync(Guid chatId, CancellationToken cancellationToken = default) =>
            inner.DeletePermanentlyAsync(chatId, cancellationToken);

        public Task<ChatInputSnapshot?> ReadInputAsync(Guid chatId, CancellationToken cancellationToken) =>
            inner.ReadInputAsync(chatId, cancellationToken);

        public Task<StoredChatInput?> ReadNewChatInputAsync(CancellationToken cancellationToken) =>
            inner.ReadNewChatInputAsync(cancellationToken);

        public Task<bool> SaveInputAsync(Guid chatId, ChatInputWrite input, CancellationToken cancellationToken) =>
            inner.SaveInputAsync(chatId, input, cancellationToken);

        public Task FreezeUploadAsync(string accountId, Guid chatId, bool isStable, CancellationToken cancellationToken) =>
            inner.FreezeUploadAsync(accountId, chatId, isStable, cancellationToken);

        public Task ApplyClearedHeadAsync(string accountId, ChatSyncHead head, CancellationToken cancellationToken) =>
            inner.ApplyClearedHeadAsync(accountId, head, cancellationToken);

        public Task<ReceivedChatCommit?> MergeSnapshotAsync(
            string accountId,
            ChatSyncDownloadedSnapshot download,
            IReadOnlySet<Guid> loadedNodes,
            bool shouldDiscardSubmission,
            CancellationToken cancellationToken) =>
            OnMergeSnapshotAsync is not null
                ? OnMergeSnapshotAsync()
                : inner.MergeSnapshotAsync(accountId, download, loadedNodes, shouldDiscardSubmission, cancellationToken);
    }
}

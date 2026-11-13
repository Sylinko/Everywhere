using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Storage;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Chat;

public sealed partial class ChatContextManager :
    ObservableObject,
    IChatContextManager,
    IRecipient<ChatContextMetadataChangedMessage>,
    IRecipient<ChatPersistenceChangedMessage>,
    IAsyncInitializer,
    IDisposable,
    IAsyncDisposable
{
    /// <summary>
    /// Stamps new retained chats even while sync is paused. Existing history keeps its stored owner.
    /// </summary>
    public string? SignedInAccountId { get; set; }

    public RootChatContext Current
    {
        get
        {
            if (_current is not null) return _current;

            CreateNew();
            return _current;
        }
    }

    public ChatMetadata? CurrentMetadata
    {
        get => Current.Data.Metadata;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (value is null) return;

            if (value.Id == Guid.Empty)
                throw new ArgumentException("The provided conversation metadata does not have a valid ID.", nameof(value));

            if (!_metadataMap.ContainsKey(value.Id))
                throw new ArgumentException("The provided conversation is not part of the history.", nameof(value));

            var previous = _current;
            if (previous?.Data.Metadata.Id == value.Id)
            {
                CancelCurrentSwitch();
                return;
            }

            var cancellation = BeginCurrentSwitch();
            OnPropertyChanged();

            SwitchCurrentAsync(value.Id, previous, cancellation).Detach(_logger.ToExceptionHandler());
        }
    }

    IRelayCommand IChatContextManager.UpdateRecentHistoryCommand => UpdateRecentHistoryCommand;

    /// <inheritdoc />
    public IReadOnlyBindableList<ChatHistorySection> AllHistory { get; }

    /// <inheritdoc />
    public string? HistorySearchQuery
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            var normalized = NormalizeHistorySearchQuery(value);
            if (string.Equals(normalized, _normalizedHistorySearchQuery, StringComparison.Ordinal)) return;

            _normalizedHistorySearchQuery = normalized;
            RestartHistoryMaterialization();
        }
    }

    /// <inheritdoc />
    public bool HistorySearchIncludesContent
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _normalizedHistorySearchQuery is null) return;

            RestartHistoryMaterialization();
        }
    }

    [ObservableProperty]
    public partial bool HasMoreItems { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial int BackgroundBusyCount { get; private set; }

    [ObservableProperty]
    public partial int BackgroundNotificationCount { get; private set; }

    [field: AllowNull, MaybeNull]
    public IRelayCommand CreateNewCommand =>
        field ??= new RelayCommand(CreateNew, () => !ReferenceEquals(_current, _newChatDraft) || !IsEmptyDraft(_current));

    /// <inheritdoc />
    public ChatHistoryView HistoryView
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                RestartHistoryMaterializationCore(resetRawHistory: true);
            }
        }
    }

    private ICollection<ChatMetadata> LoadedMetadata => _metadataMap.Values;

    /// <summary>
    /// Signals eligible edits or completed root work. A connected sync session coalesces
    /// these signals and flushes local persistence before any network publication.
    /// </summary>
    public event EventHandler? SyncRequested;

    private RootChatContext? _current;
    private RootChatContext? _newChatDraft;
    private CancellationTokenSource? _currentSwitchCancellation;

    private readonly ConcurrentDictionary<Guid, ChatMetadata> _metadataMap = [];
    private readonly SourceList<ChatMetadata> _materializedHistorySource = new();
    private readonly Subject<Unit> _historyRegrouper = new();
    private readonly IDisposable _historyConnection;
    private readonly SemaphoreSlim _historyLoadGate = new(1, 1);
    private readonly Lock _historyStateLock = new();
    private readonly List<Guid> _historyScanIds = [];
    private readonly HashSet<Guid> _rawHistoryIds = [];
    private readonly HashSet<Guid> _materializedHistoryIds = [];
    private readonly HashSet<Guid> _busyContexts = [];
    private readonly HashSet<Guid> _notificationContexts = [];

    /// <summary>
    /// Uncommitted save notifications. Entries retain their first account and canonical root
    /// until successful commit; a later notification replaces the entry identity, not its owner.
    /// </summary>
    private readonly Dictionary<Guid, PendingSave> _pendingSaves = [];

    // Snapshots are captured on input change. They share the ordinary save gate/debounce and
    // retain their root until committed, but never request synchronization or create history.
    private readonly Dictionary<Guid, PendingInput> _pendingInputs = [];

    // Accepted local edits finish independently of sync or navigation cancellation.
    // Shutdown waits for these commits and their publication before releasing the roots.
    private readonly SemaphoreSlim _commitGate = new(1, 1);

    private readonly Settings _settings;
    private readonly IChatContextStorage _chatContextStorage;
    private readonly ILogger<ChatContextManager> _logger;
    private readonly DebounceExecutor<ChatContextManager, ThreadingTimerImpl> _saveDebounceExecutor;

    private readonly Lock _rootsLock = new();
    private readonly Dictionary<Guid, ResilientReference<RootChatContext>> _roots = [];
    private readonly Dictionary<Task<RootChatContext>, PendingContextLoad> _loads = [];

    private CancellationTokenSource _historyGenerationCancellation = new();
    private string? _normalizedHistorySearchQuery;
    private ChatHistoryCursor? _storageCursor;
    private int _historyScanIndex;
    private int _historyGeneration;
    private int _activeHistorySessions;
    private bool _rawHistoryExhausted;
    private Task? _disposeTask;

    private volatile bool _isClosing;
    private volatile bool _isDisposed;

    public ChatContextManager(
        Settings settings,
        IChatContextStorage chatContextStorage,
        ILogger<ChatContextManager> logger)
    {
        _settings = settings;
        _chatContextStorage = chatContextStorage;
        _logger = logger;

        AllHistory = _materializedHistorySource
            .Connect()
            .AutoRefresh(static metadata => metadata.DateModified)
            .ObserveOnAvaloniaDispatcher()
            .GroupOn(GetHistorySection, _historyRegrouper)
            .Transform(static group => new ChatHistorySection(group))
            .DisposeMany()
            .Sort(SortExpressionComparer<ChatHistorySection>.Ascending(static history => history.Kind))
            .BindEx(out _historyConnection);

        _saveDebounceExecutor = new DebounceExecutor<ChatContextManager, ThreadingTimerImpl>(
            () => this,
            static that => that.FlushPendingSavesAsync().Detach(that._logger.ToExceptionHandler()),
            TimeSpan.FromSeconds(0.5))
        {
            // A continuously streaming response must still be persisted periodically even
            // though every new token resets the trailing debounce interval.
            MaximumDelay = TimeSpan.FromSeconds(2)
        };

        WeakReferenceMessenger.Default.Register<ChatContextMetadataChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<ChatPersistenceChangedMessage>(this);

        Task.Run(CleanupUnusedWorkingDirectories).Detach(logger.ToExceptionHandler());
    }

    /// <summary>
    /// Handles chat data changed events.
    /// </summary>
    /// <param name="message"></param>
    public void Receive(ChatContextMetadataChangedMessage message)
    {
        if (_isDisposed)
        {
            return;
        }

        if (message.PropertyName == nameof(ChatMetadata.States))
        {
            // Running roots remain observable even when their history view is not selected.
            // Nested runtime states are presented inside their root, not counted as separate chats.
            if (FindRoot(message.Metadata) is null)
            {
                return;
            }
        }
        else if (!_metadataMap.TryGetValue(message.Metadata.Id, out var metadata) || !ReferenceEquals(metadata, message.Metadata))
        {
            return;
        }

        switch (message.PropertyName)
        {
            case nameof(ChatMetadata.States):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    if (message.Metadata.States.HasFlag(ChatContextMetadataStates.Busy))
                    {
                        _busyContexts.Add(message.Metadata.Id);
                    }
                    else
                    {
                        _busyContexts.Remove(message.Metadata.Id);
                    }
                    if (message.Metadata.States.HasFlag(ChatContextMetadataStates.HasNotification)) _notificationContexts.Add(message.Metadata.Id);
                    else _notificationContexts.Remove(message.Metadata.Id);

                    var currentId = _current?.Data.Metadata.Id;
                    BackgroundBusyCount = _busyContexts.AsValueEnumerable().Count(id => id != currentId);
                    BackgroundNotificationCount = _notificationContexts.AsValueEnumerable().Count(id => id != currentId);
                });
                break;
            }
            case nameof(ChatMetadata.IsTemporary):
            case nameof(ChatMetadata.ArchivedAt):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    if (!_metadataMap.ContainsKey(message.Metadata.Id))
                    {
                        return;
                    }

                    if (message.PropertyName == nameof(ChatMetadata.IsTemporary) && FindRoot(message.Metadata) is { } root)
                    {
                        HandleInputChanged(root.Input, EventArgs.Empty);
                    }

                    if (message.PropertyName == nameof(ChatMetadata.IsTemporary) && HistoryView != ChatHistoryView.Temporary)
                    {
                        // Retention does not change membership or ordering in Recent/Archived.
                        // Only an untitled chat's localized fallback can change a search match.
                        if (_normalizedHistorySearchQuery is not null && string.IsNullOrWhiteSpace(message.Metadata.Topic))
                        {
                            RestartHistoryMaterializationCore();
                        }
                        return;
                    }

                    if (_normalizedHistorySearchQuery is not null)
                    {
                        // Re-evaluate membership and search together, without a second restart in AddRecentHistoryMetadata.
                        RestartHistoryMaterializationCore(resetRawHistory: true);
                        return;
                    }

                    if (!MatchesHistoryView(message.Metadata))
                    {
                        RemoveHistoryMetadata(message.Metadata);
                    }
                    RestartHistoryMaterializationCore(resetRawHistory: true, preserveVisibleItems: true);
                    AddRecentHistoryMetadata(message.Metadata);
                });
                break;
            }
            case nameof(ChatMetadata.IsPinned):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    _historyRegrouper.OnNext(Unit.Default);
                    RestartHistoryMaterializationCore(resetRawHistory: true, preserveVisibleItems: true);
                });
                break;
            }
            case nameof(ChatMetadata.DateModified):
            case nameof(ChatMetadata.Topic):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    CreateNewCommand.NotifyCanExecuteChanged();
                    // AutoRefresh handles timestamp ordering. Content matches refresh after saving;
                    // only a title change needs to re-evaluate this query immediately.
                    if (message.PropertyName == nameof(ChatMetadata.Topic) && _normalizedHistorySearchQuery is not null)
                    {
                        RestartHistoryMaterializationCore();
                    }
                });
                break;
            }
        }
    }

    /// <summary>
    /// Retains pending local changes independently of presentation notifications.
    /// </summary>
    public void Receive(ChatPersistenceChangedMessage message)
    {
        if (_isDisposed)
        {
            return;
        }
        if (!_metadataMap.TryGetValue(message.Metadata.Id, out var metadata) || !ReferenceEquals(metadata, message.Metadata))
        {
            return;
        }
        if (!QueueSave(message.Metadata))
        {
            return;
        }

        if (!_isClosing)
        {
            _saveDebounceExecutor.Trigger();
            if (metadata.CaptureChanges().HasIndependentFields)
            {
                SyncRequested?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <inheritdoc />
    public Task FlushPendingSavesAsync()
    {
        // Serialization and SQLite work must not run synchronously on the UI caller. The gate
        // includes batch capture so a final flush also waits for any earlier in-flight write.
        return Task.Run(() => FlushPendingSavesCoreAsync(shouldRequestSync: true));
    }

    internal Task FlushSyncSavesAsync() => Task.Run(() => FlushPendingSavesCoreAsync(shouldRequestSync: false));

    private async Task FlushPendingSavesCoreAsync(bool shouldRequestSync)
    {
        bool hasStableWrites;
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isClosing) return;
            hasStableWrites = await CommitPendingSavesAsync().ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
        if (shouldRequestSync && hasStableWrites)
        {
            SyncRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<bool> CommitPendingSavesAsync()
    {
        // The caller holds _commitGate. Shutdown uses this same batch path after draining roots.
        PendingSave[] pendingSaves;
        lock (_pendingSaves)
        {
            pendingSaves = [.. _pendingSaves.Values];
        }

        var failures = new List<Exception>();
        foreach (var pendingSave in pendingSaves)
        {
            try
            {
                await SavePendingAsync(pendingSave).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Failed to save conversation {pendingSave.Metadata.Id}.", exception));
            }
        }

        // A failed message save must not consume its composer. Successful graph writes already
        // acknowledged the input captured in their transaction; only independent edits remain.
        if (failures.Count == 0)
        {
            await CommitPendingInputsAsync(failures).ConfigureAwait(false);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Some conversations could not be saved.", failures);
        }
        return pendingSaves.Any(pendingSave => pendingSave.Root?.HasWork != true);
    }

    private async Task SavePendingAsync(PendingSave pendingSave)
    {
        var capture = await CapturePendingSaveAsync(pendingSave).ConfigureAwait(false);
        await CommitPendingSaveAsync(pendingSave, capture).ConfigureAwait(false);
    }

    private static async Task<CapturedChatWrite> CapturePendingSaveAsync(PendingSave pendingSave)
    {
        if (pendingSave.Root is { } root)
        {
            return await root.DispatchAsync(() => ChatWriteBatch.Capture(root.Data)).ConfigureAwait(false);
        }

        var metadata = ChatMetadataWrite.Capture(pendingSave.Metadata, out var changes);
        return new CapturedChatWrite(new ChatWriteBatch(metadata, []), [], changes);
    }

    private async Task CommitPendingSaveAsync(PendingSave pendingSave, CapturedChatWrite capture)
    {
        if (pendingSave.Root is { } root)
        {
            var input = GetPendingInput(pendingSave.Metadata.Id);
            await _chatContextStorage.SaveChatContextAsync(
                    capture.Batch with { LocalAccountId = pendingSave.LocalAccountId, LocalInput = input?.Write },
                    CancellationToken.None)
                .ConfigureAwait(false);
            await root.DispatchAsync(() =>
            {
                pendingSave.Metadata.IsPersisted = true;
                root.Data.PersistenceDirtyState.Acknowledge(capture.Changes);
                pendingSave.Metadata.Acknowledge(capture.MetadataChanges);
            }).ConfigureAwait(false);
            if (input is not null)
            {
                AcknowledgeInput(input);
            }
        }
        else
        {
            await _chatContextStorage.SaveChatContextMetadataAsync(capture.Batch.Metadata, CancellationToken.None).ConfigureAwait(false);
            pendingSave.Metadata.Acknowledge(capture.MetadataChanges);
        }

        lock (_pendingSaves)
        {
            // A failed write stays queued automatically. A successful write consumes only its
            // own notification, leaving any later notification and its unacknowledged edits intact.
            if (_pendingSaves.TryGetValue(pendingSave.Metadata.Id, out var current) && ReferenceEquals(current, pendingSave))
            {
                _pendingSaves.Remove(pendingSave.Metadata.Id);
            }
        }

        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isClosing) return;
            if (_newChatDraft is not null && ReferenceEquals(_newChatDraft, pendingSave.Root))
            {
                _newChatDraft = null;
            }
            if (HistorySearchIncludesContent && _normalizedHistorySearchQuery is not null)
            {
                RestartHistoryMaterializationCore();
            }
        });
    }

    private async Task SavePendingChatAsync(Guid id)
    {
        if (GetPendingSave(id) is { } pending)
        {
            await SavePendingAsync(pending).ConfigureAwait(false);
        }
    }

    private PendingSave? GetPendingSave(Guid id)
    {
        lock (_pendingSaves)
        {
            return _pendingSaves.GetValueOrDefault(id);
        }
    }

    private static bool CanSave(ChatMetadata metadata, RootChatContext? root) => metadata.IsPersisted || root is { Data.HasHistory: true };

    private bool QueueSave(ChatMetadata metadata)
    {
        // Capture the owner at notification ingress, while the writer still holds it. A pending
        // or failed save must not rely on a later lookup through the manager's weak index.
        var root = FindRoot(metadata);
        var localAccountId = SignedInAccountId;
        lock (_pendingSaves)
        {
            if (!_metadataMap.TryGetValue(metadata.Id, out var canonical) || !ReferenceEquals(canonical, metadata))
            {
                return false;
            }

            // Draft edits remain dirty on the model. Its first real node queues the complete
            // initial write; merely changing title or retention must not create a stored chat.
            if (!CanSave(metadata, root))
            {
                return false;
            }

            if (_pendingSaves.TryGetValue(metadata.Id, out var pending))
            {
                // Preserve initial ownership, including when the account changes during I/O.
                // Always publish a new entry identity so an older commit cannot consume this edit.
                root = pending.Root ?? root;
                localAccountId = pending.LocalAccountId;
            }
            _pendingSaves[metadata.Id] = new PendingSave(root, metadata, localAccountId);
            return true;
        }
    }

    /// <summary>
    /// Removes empty dated plugin working directories older than three days.
    /// </summary>
    private void CleanupUnusedWorkingDirectories()
    {
        var regex = WorkingDirectoryRegex();
        var pluginsDir = RuntimeConstants.EnsureWritableDataFolderPath("plugins");
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!regex.IsMatch(dirName)) continue;

            if (!DateTime.TryParseExact(dirName, "yyyy-MM-dd", null, DateTimeStyles.None, out var dirDate))
                continue;

            // If the directory is 3 days later and is empty, delete it
            if ((DateTime.Now - dirDate).TotalDays > 3 && !Directory.EnumerateFileSystemEntries(dir).AsValueEnumerable().Any())
            {
                try
                {
                    Directory.Delete(dir); // do not use recursive delete
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unused working directory: {Directory}", dir);
                }
            }
        }
    }

    [RelayCommand]
    private void UpdateRecentHistory() =>
        Dispatcher.UIThread.PostOnDemand(() => RestartHistoryMaterializationCore(resetRawHistory: true));

    [MemberNotNull(nameof(_current))]
    private void CreateNew()
    {
        CancelCurrentSwitch();
        if (IsEmptyDraft(_newChatDraft) && _metadataMap.ContainsKey(_newChatDraft.Data.Metadata.Id))
        {
            PublishCurrent(_newChatDraft);
            AddRecentHistoryMetadata(_newChatDraft.Data.Metadata);
            return;
        }
        _newChatDraft = null;

        var isCurrentTemporary = _current?.Data.Metadata.IsTemporary is true;
        var isTemporary = _settings.ChatWindow.TemporaryChatMode switch
        {
            TemporaryChatMode.RememberLast => isCurrentTemporary,
            TemporaryChatMode.Always => true,
            _ => false
        };
        var root = CreateBlankRoot(isTemporary);
        _newChatDraft = root;
        PublishCurrent(root);
        AddRecentHistoryMetadata(root.Data.Metadata);
    }

    /// <inheritdoc />
    public RootChatContext CreateChat(bool isTemporary)
    {
        CancelCurrentSwitch();
        var root = CreateBlankRoot(isTemporary);
        PublishCurrent(root);
        AddRecentHistoryMetadata(root.Data.Metadata);
        return root;
    }

    private RootChatContext CreateBlankRoot(bool isTemporary)
    {
        var data = new ChatData();
        data.Metadata.IsTemporary = isTemporary;
        return CreateRoot(data);
    }

    [MemberNotNull(nameof(_current))]
    private void PublishCurrent(RootChatContext next)
    {
        Dispatcher.UIThread.VerifyAccess();
        var previous = _current;
        _current = next;
        NotifyCurrentChanged();
        if (previous is null || ReferenceEquals(previous, next))
        {
            return;
        }

        if (IsEmptyDraft(previous))
        {
            RemoveHistoryMetadata(previous.Data.Metadata);
            if (!ReferenceEquals(previous, _newChatDraft))
            {
                _metadataMap.TryRemove(previous.Data.Metadata.Id, out _);
            }
        }

        // Current keeps a deleted root alive during preparation. Once bindings have moved,
        // the normal publication path releases it; deletion does not await later navigation.
        if (!_metadataMap.ContainsKey(previous.Data.Metadata.Id))
        {
            ReleaseRoot(previous);
        }
    }

    private void ReleaseRoot(RootChatContext root)
    {
        using (_rootsLock.EnterScope())
        {
            if (!_roots.Remove(root.Data.Metadata.Id)) return;
        }
        root.Idle -= OnRootIdle;
        root.Input.Changed -= HandleInputChanged;
        root.DisposeAsync().AsTask().Detach(_logger.ToExceptionHandler());
    }

    /// <inheritdoc />
    public async Task RenameAsync(ChatMetadata metadata, string? topic)
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            if (!_metadataMap.TryGetValue(metadata.Id, out var canonical) || !ReferenceEquals(canonical, metadata))
            {
                return;
            }

            RootChatContext? root;
            using (_rootsLock.EnterScope())
            {
                root = _roots.GetValueOrDefault(metadata.Id)?.Target;
            }

            if (root is null)
            {
                metadata.Rename(topic);
            }
            else
            {
                await root.ApplyAsync(() => metadata.Rename(topic)).ConfigureAwait(false);
            }
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <inheritdoc />
    public Task SetPinnedAsync(ChatMetadata metadata, bool isPinned)
    {
        if (IsEmptyDraft(FindRoot(metadata)))
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            await _commitGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_isClosing, this);
                if (!_metadataMap.TryGetValue(metadata.Id, out var canonical) || !ReferenceEquals(canonical, metadata) ||
                    metadata.IsPinned == isPinned)
                {
                    return;
                }

                await SavePendingChatAsync(metadata.Id).ConfigureAwait(false);
                await _chatContextStorage.SetPinnedAsync(metadata.Id, isPinned, CancellationToken.None).ConfigureAwait(false);
                // Publication is part of the committed operation, not a cancellable UI wait.
                var root = FindRoot(metadata);
                if (root is null)
                {
                    metadata.IsPinned = isPinned;
                }
                else
                {
                    await root.DispatchAsync(() => metadata.IsPinned = isPinned).ConfigureAwait(false);
                }
                SyncRequested?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _commitGate.Release();
            }
        });
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(ChatMetadata metadata)
    {
        if (IsEmptyDraft(FindRoot(metadata)))
        {
            return;
        }

        if (metadata.IsTemporary)
        {
            throw new InvalidOperationException("Temporary conversations cannot be archived.");
        }

        await Task.Run(async () =>
        {
            await _commitGate.WaitAsync().ConfigureAwait(false);
            var root = FindRoot(metadata);
            try
            {
                ObjectDisposedException.ThrowIf(_isClosing, this);
                if (root is not null)
                {
                    await root.StopAsync().ConfigureAwait(false);
                }
                await SavePendingChatAsync(metadata.Id).ConfigureAwait(false);
                // Commit model state before releasing the persistence gate. History projection
                // observes the notification independently; it is not part of this transaction.
                var archivedAt = await _chatContextStorage.ArchiveAsync(metadata.Id, CancellationToken.None).ConfigureAwait(false);
                await PublishArchiveStateAsync(metadata, archivedAt).ConfigureAwait(false);
                SyncRequested?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                if (!_isClosing)
                {
                    root?.ResumeAfterStop();
                }
                _commitGate.Release();
            }
        });
        if (_current?.Data.Metadata.Id == metadata.Id)
        {
            await LoadRecentAsCurrentAsync();
        }
    }

    /// <summary>Whether stopping sync now could strand work not represented in the inventory.</summary>
    public bool HasUncommittedWork(Guid chatId)
    {
        lock (_pendingSaves)
        {
            if (_pendingSaves.ContainsKey(chatId))
            {
                return true;
            }
        }
        return FindRoot(chatId)?.HasWork == true;
    }

    /// <inheritdoc />
    public bool HasRunningWork(ChatMetadata metadata) => FindRoot(metadata)?.HasWork == true;

    /// <inheritdoc />
    public async Task RestoreAsync(ChatMetadata metadata)
    {
        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            await _chatContextStorage.RestoreAsync(metadata.Id, CancellationToken.None).ConfigureAwait(false);
            await PublishArchiveStateAsync(metadata, null).ConfigureAwait(false);
            SyncRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    private async Task PublishArchiveStateAsync(ChatMetadata metadata, DateTimeOffset? archivedAt)
    {
        // The persistence gate keeps topology installation and deletion outside this publication.
        RootChatContext? root;
        using (_rootsLock.EnterScope())
        {
            root = _roots.GetValueOrDefault(metadata.Id)?.Target;
        }

        if (root is null)
        {
            metadata.ArchivedAt = archivedAt;
        }
        else
        {
            await root.DispatchAsync(() => metadata.ArchivedAt = archivedAt).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task DeletePermanentlyAsync(ChatMetadata metadata)
    {
        return Task.Run(async () =>
        {
            await _commitGate.WaitAsync().ConfigureAwait(false);
            RootChatContext? root;
            using (_rootsLock.EnterScope())
            {
                root = _roots.GetValueOrDefault(metadata.Id)?.Target;
            }

            try
            {
                ObjectDisposedException.ThrowIf(_isClosing, this);
                if (root is not null)
                {
                    root.BeginDeletion();
                    await root.StopAsync().ConfigureAwait(false);
                }

                try
                {
                    await _chatContextStorage.DeletePermanentlyAsync(metadata.Id, CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    using (_rootsLock.EnterScope())
                    {
                        if (!_isClosing)
                        {
                            root?.ResumeAfterStop();
                        }
                    }

                    throw;
                }

                await RemoveDeletedChatAsync(metadata.Id, root).ConfigureAwait(false);
                SyncRequested?.Invoke(this, EventArgs.Empty);
            }
            finally
            {
                _commitGate.Release();
            }
        });
    }

    private async Task RemoveDeletedChatAsync(Guid chatId, RootChatContext? root)
    {
        CancellationTokenSource historyCancellation;
        ChatMetadata? removed;
        lock (_historyStateLock)
        {
            _historyGeneration++;
            historyCancellation = _historyGenerationCancellation;
            lock (_pendingSaves)
            {
                _metadataMap.TryRemove(chatId, out removed);
                _pendingSaves.Remove(chatId);
                lock (_pendingInputs)
                {
                    _pendingInputs.Remove(chatId);
                }
            }
        }

        await historyCancellation.CancelAsync().ConfigureAwait(false);
        if (root is not null && !ReferenceEquals(_current, root))
        {
            ReleaseRoot(root);
        }
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (removed is not null)
            {
                RemoveHistoryMetadata(removed);
            }
            RestartHistoryMaterialization();
            QueueLeaveUnavailableChat(chatId);
        });
    }

    /// <inheritdoc />
    public async Task PersistChatAsync(ChatMetadata metadata)
    {
        var root = await GetOrLoadContextAsync(metadata, CancellationToken.None).ConfigureAwait(false);
        if (!root.Data.HasHistory && !metadata.IsPersisted)
        {
            // New drafts only select retention for future messages. Stored empty histories still have a row to update.
            await root.ApplyAsync(() => metadata.SetTemporary(false)).ConfigureAwait(false);
            return;
        }

        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            if (!metadata.IsTemporary)
            {
                return;
            }

            // Capture through the existing writer. Only the detached batch changes retention before commit.
            var (batch, changes, metadataChanges) = await root.DispatchAsync(() => ChatWriteBatch.Capture(root.Data)).ConfigureAwait(false);
            batch.Metadata.Snapshot.IsTemporary = false;
            batch = batch with
            {
                LocalAccountId = SignedInAccountId,
                Metadata = batch.Metadata with
                {
                    ShouldWriteRetention = true
                }
            };
            await _chatContextStorage.SaveChatContextAsync(batch, CancellationToken.None).ConfigureAwait(false);
            await root.DispatchAsync(() =>
            {
                metadata.IsPersisted = true;
                root.Data.PersistenceDirtyState.Acknowledge(changes);
                metadata.Acknowledge(metadataChanges);
                metadata.IsTemporary = false;
            }).ConfigureAwait(false);
            SyncRequested?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// Loads the most recently modified chat data as current.
    /// </summary>
    private async Task LoadRecentAsCurrentAsync()
    {
        if (_isClosing) return;
        using var cancellation = BeginCurrentSwitch();
        var previous = _current;
        try
        {
            var historyItem = LoadedMetadata
                .AsValueEnumerable()
                .Where(metadata => !metadata.IsArchived)
                .OrderByDescending(metadata => metadata.DateModified)
                .ThenByDescending(metadata => metadata.Id)
                .FirstOrDefault();
            var candidate = await _chatContextStorage.GetMostRecentChatMetadataAsync(cancellation.Token);
            if (candidate is not null)
            {
                var canonical = _metadataMap.GetOrAdd(candidate.Id, candidate);
                if (historyItem is null || canonical.DateModified > historyItem.DateModified ||
                    canonical.DateModified == historyItem.DateModified && canonical.Id.CompareTo(historyItem.Id) > 0)
                {
                    historyItem = canonical;
                }
            }

            cancellation.Token.ThrowIfCancellationRequested();
            var next = historyItem is null ? null : await LoadChatContextAsync(historyItem.Id, cancellation.Token);
            await PrepareAndPublishCurrentAsync(next, previous, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            FinishCurrentSwitch(cancellation);
        }
    }

    public Task<RootChatContext?> LoadChatContextAsync(ChatMetadata metadata, CancellationToken cancellationToken = default)
    {
        _metadataMap.TryAdd(metadata.Id, metadata);
        return metadata.Id == _current?.Data.Metadata.Id ?
            Task.FromResult<RootChatContext?>(_current) :
            LoadChatContextAsync(metadata.Id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ViewSyncedBranchAsync(RootChatContext root, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isClosing, this);
        await root.ApplyAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            root.Data.FollowDurableChoices();
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private CancellationTokenSource BeginCurrentSwitch()
    {
        CancelCurrentSwitch();
        var cancellation = new CancellationTokenSource();
        _currentSwitchCancellation = cancellation;
        return cancellation;
    }

    private void CancelCurrentSwitch()
    {
        var cancellation = _currentSwitchCancellation;
        _currentSwitchCancellation = null;
        // The async switch owns disposal. Cancel its waits, not the canonical data or shared parsing.
        cancellation?.Cancel();
    }

    private void FinishCurrentSwitch(CancellationTokenSource cancellation)
    {
        if (!ReferenceEquals(_currentSwitchCancellation, cancellation)) return;
        _currentSwitchCancellation = null;
        // A failed selection may retain an existing chat, but never a deleted one.
        if (!_isClosing && _current is { } current && !_metadataMap.ContainsKey(current.Data.Metadata.Id))
        {
            CreateNew();
        }
    }

    private async Task SwitchCurrentAsync(Guid id, RootChatContext? previous, CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            try
            {
                var next = await LoadChatContextAsync(id, cancellation.Token);
                await PrepareAndPublishCurrentAsync(next, previous, cancellation);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                FinishCurrentSwitch(cancellation);
            }
        }
    }

    private async Task PrepareAndPublishCurrentAsync(
        RootChatContext? next,
        RootChatContext? previous,
        CancellationTokenSource cancellation)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (next is not null)
        {
            if (next.Input.Edit is { } edit && next.Data.FindNode(edit.NodeId) is { IsMessageLoaded: false } target)
            {
                await next.EnsureTurnLoadedAsync(target.Turn, cancellation.Token).ConfigureAwait(false);
            }
            if (!ReferenceEquals(next, previous))
            {
                await ViewSyncedBranchAsync(next, cancellation.Token).ConfigureAwait(false);
            }
            // Preparation and publication remain UI-owned; canceled waits do not dispose the model.
            var isPrepared = await Dispatcher.UIThread.InvokeAsync(
                () => next.Presentation.PrepareInitialWindowAsync(cancellation.Token),
                DispatcherPriority.Normal,
                cancellation.Token).GetTask().Unwrap().WaitAsync(cancellation.Token);
            if (!isPrepared)
            {
                return;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                if (_isClosing || cancellation.IsCancellationRequested ||
                    !ReferenceEquals(_currentSwitchCancellation, cancellation) ||
                    next is not null && (next.Data.IsDisposed || !_metadataMap.ContainsKey(next.Data.Metadata.Id)))
                {
                    return;
                }

                if (next is null)
                {
                    CreateNew();
                }
                else
                {
                    PublishCurrent(next);
                }
            },
            DispatcherPriority.Normal,
            cancellation.Token);
    }

    private async Task<RootChatContext> GetOrLoadContextAsync(ChatMetadata metadata, CancellationToken cancellationToken)
    {
        Task<RootChatContext> loading;
        using (_rootsLock.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            if (_roots.GetValueOrDefault(metadata.Id)?.Target is { } existing)
            {
                return existing;
            }

            // Topology has no runtime yet. The manager owns this I/O until a canonical root takes
            // over, so shutdown can cancel and drain it without blocking unrelated saves/archives.
            var pending = new PendingContextLoad(metadata.Id, CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
            loading = Task.Run(() => LoadContextCoreAsync(metadata, pending), CancellationToken.None);
            _loads.Add(loading, pending);
        }

        try
        {
            return await loading.ConfigureAwait(false);
        }
        finally
        {
            using var scope = _rootsLock.EnterScope();
            if (_loads.Remove(loading, out var pending))
            {
                pending.Cancellation.Dispose();
            }
        }
    }

    private async Task<RootChatContext> LoadContextCoreAsync(ChatMetadata metadata, PendingContextLoad pending)
    {
        var cancellationToken = pending.Cancellation.Token;
        while (true)
        {
            using (_rootsLock.EnterScope())
            {
                pending.ShouldReload = false;
            }

            var data = metadata.IsPersisted ?
                await _chatContextStorage.GetChatContextAsync(metadata, cancellationToken).ConfigureAwait(false) :
                new ChatData(metadata, [], new ChatMessageNode(Guid.NewGuid().SetVersion(0), new RootChatMessage()));
            try
            {
                var input = await _chatContextStorage.ReadInputAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
                await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    using var scope = _rootsLock.EnterScope();
                    cancellationToken.ThrowIfCancellationRequested();
                    ObjectDisposedException.ThrowIf(_isClosing, this);
                    if (!_metadataMap.ContainsKey(metadata.Id))
                    {
                        throw new OperationCanceledException("The conversation was removed while loading.");
                    }
                    if (_roots.GetValueOrDefault(metadata.Id)?.Target is { } existing)
                    {
                        if (!ReferenceEquals(data, existing.Data))
                        {
                            data.Dispose();
                        }
                        return existing;
                    }
                    if (pending.ShouldReload)
                    {
                        // Sync committed while this detached read was in flight. Retry outside
                        // the commit gate; a slow history read must not block unrelated saves/archive.
                        data.Dispose();
                        continue;
                    }
                    var root = CreateRoot(data);
                    if (input is not null)
                    {
                        root.Input.Restore(input);
                    }
                    return root;
                }
                finally
                {
                    _commitGate.Release();
                }
            }
            catch
            {
                data.Dispose();
                throw;
            }
        }
    }

    private async Task<RootChatContext?> LoadChatContextAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var context = await GetOrLoadContextAsync(_metadataMap[id], cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return context;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to load chat data {ChatContextId}", id);

            Dispatcher.UIThread.PostOnDemand(() =>
            {
                if (_isClosing || cancellationToken.IsCancellationRequested) return;
                ToastManager
                    .Error(
                        LocaleKey.Common_Error.I18N(),
                        new FormattedDynamicLocaleKey(
                            LocaleKey.ChatContextManager_LoadChatContextFailedToast_Content,
                            ex.GetFriendlyMessage()));
            });

            return null;
        }
    }

    /// <summary>
    /// Notifies that the current chat data has changed.
    /// </summary>
    private void NotifyCurrentChanged()
    {
        Dispatcher.UIThread.VerifyAccess();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentMetadata));
        CreateNewCommand.NotifyCanExecuteChanged();
        var currentId = _current?.Data.Metadata.Id;
        BackgroundBusyCount = _busyContexts.AsValueEnumerable().Count(id => id != currentId);
        BackgroundNotificationCount = _notificationContexts.AsValueEnumerable().Count(id => id != currentId);
    }

    /// <inheritdoc />
    public IIncrementalLoadSession BeginLoadSession()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Interlocked.Increment(ref _activeHistorySessions);
        Dispatcher.UIThread.PostOnDemand(UpdateHistoryBusyState);
        return new IncrementalLoadSession(this);
    }

    private async ValueTask<IncrementalLoadResult> LoadMoreHistoryAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ObjectDisposedException.ThrowIf(_isClosing, this);

        await _historyLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_isClosing)
                {
                    throw new OperationCanceledException();
                }

                int generation;
                string? query;
                bool searchIncludesContent;
                CancellationToken generationToken;
                lock (_historyStateLock)
                {
                    if (_isClosing) throw new OperationCanceledException();
                    generation = _historyGeneration;
                    query = _normalizedHistorySearchQuery;
                    searchIncludesContent = HistorySearchIncludesContent;
                    generationToken = _historyGenerationCancellation.Token;
                }

                try
                {
                    return await LoadMoreHistoryCoreAsync(
                        count,
                        generation,
                        query,
                        searchIncludesContent,
                        generationToken,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!_isClosing && generationToken.IsCancellationRequested &&
                                                         !cancellationToken.IsCancellationRequested)
                {
                    // Query changes are internal retargeting, not cancellation of the caller's
                    // viewport-fill operation. Retry the same requested page against the latest
                    // generation so the behavior remains independent of search state.
                }
            }
        }
        catch (OperationCanceledException) when (_isClosing || cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to incrementally load chat data history");
            ToastManager.Error(LocaleKey.Common_Error.I18N(), ex.GetFriendlyMessage());

            return new IncrementalLoadResult(0, HasMoreItems);
        }
        finally
        {
            _historyLoadGate.Release();
        }
    }

    private async Task<IncrementalLoadResult> LoadMoreHistoryCoreAsync(
        int count,
        int generation,
        string? query,
        bool searchIncludesContent,
        CancellationToken generationToken,
        CancellationToken callerToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(generationToken, callerToken);
        var cancellationToken = linkedCancellation.Token;
        var matches = new List<ChatMetadata>(count);
        var pattern = query is null ? null : new TextSearchPattern(query);
        int scanIndex;
        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            scanIndex = _historyScanIndex;
        }

        while (matches.Count < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid? candidateId = null;
            var mustFetch = false;
            lock (_historyStateLock)
            {
                ThrowIfHistoryGenerationChanged(generation, cancellationToken);
                if (scanIndex < _historyScanIds.Count)
                {
                    candidateId = _historyScanIds[scanIndex++];
                    if (_materializedHistoryIds.Contains(candidateId.Value))
                    {
                        continue;
                    }
                }
                else if (!_rawHistoryExhausted)
                {
                    mustFetch = true;
                }
            }

            if (mustFetch)
            {
                await FetchRawHistoryPageAsync(Math.Max(count - matches.Count, 20), generation, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (candidateId is not { } id) break;
            if (!_metadataMap.TryGetValue(id, out var metadata) || !MatchesHistoryView(metadata)) continue;
            if (await MatchesHistorySearchAsync(metadata, pattern, searchIncludesContent, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(metadata);
            }
        }

        bool hasMoreItems;
        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            hasMoreItems = scanIndex < _historyScanIds.Count || !_rawHistoryExhausted;
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                lock (_historyStateLock)
                {
                    ThrowIfHistoryGenerationChanged(generation, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();

                    matches.RemoveAll(metadata =>
                        !MatchesHistoryView(metadata) ||
                        !_metadataMap.ContainsKey(metadata.Id) ||
                        !_materializedHistoryIds.Add(metadata.Id));
                    if (matches.Count > 0)
                    {
                        _materializedHistorySource.AddRange(matches);
                    }

                    // Consuming candidates and publishing this page are one commit. Cancellation
                    // before publication leaves the scan position available to the next request.
                    _historyScanIndex = scanIndex;
                    HasMoreItems = hasMoreItems;
                    return new IncrementalLoadResult(matches.Count, hasMoreItems);
                }
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task FetchRawHistoryPageAsync(
        int count,
        int generation,
        CancellationToken cancellationToken)
    {
        ChatHistoryCursor? cursor;
        lock (_historyStateLock)
        {
            cursor = _storageCursor;
        }

        var page = await _chatContextStorage.ReadHistoryPageAsync(
            count,
            cursor,
            HistoryView,
            cancellationToken).ConfigureAwait(false);

        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            foreach (var metadata in page.Items)
            {
                var canonical = _metadataMap.GetOrAdd(metadata.Id, metadata);

                if (_rawHistoryIds.Add(canonical.Id) && MatchesHistoryView(canonical))
                {
                    _historyScanIds.Add(canonical.Id);
                }
            }

            _storageCursor = page.NextCursor;
            _rawHistoryExhausted = page.NextCursor is null;
            IncludeCurrentHistoryCandidate();
        }
    }

    private void IncludeCurrentHistoryCandidate()
    {
        // Called under the history lock after the database prefix is complete. Local empty or
        // not-yet-saved chats remain searchable, without letting cached rows precede unread pins.
        if (_rawHistoryExhausted && _current is { } current && _metadataMap.ContainsKey(current.Data.Metadata.Id) &&
            MatchesHistoryView(current.Data.Metadata) && _rawHistoryIds.Add(current.Data.Metadata.Id))
        {
            _historyScanIds.Add(current.Data.Metadata.Id);
        }
    }

    private async ValueTask<bool> MatchesHistorySearchAsync(
        ChatMetadata metadata,
        TextSearchPattern? pattern,
        bool includesContent,
        CancellationToken cancellationToken)
    {
        if (pattern is null) return true;
        if (metadata.ActualTopic?.Contains(pattern.Query, StringComparison.OrdinalIgnoreCase) is true) return true;
        if (!includesContent) return false;

        try
        {
            var context = metadata.Id == _current?.Data.Metadata.Id ?
                _current :
                await GetOrLoadContextAsync(metadata, cancellationToken).ConfigureAwait(false);
            return context is not null && await ChatTextSearcher.ContainsAsync(
                context,
                pattern,
                ChatTextSearcher.SharedMarkdownTextProjector,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Filtering is read-only and best-effort. One damaged history entry must not abort the
            // ordered scan or reuse the interactive load path that shows a toast.
            _logger.LogWarning(ex, "Failed to inspect chat data {ChatContextId} while filtering history", metadata.Id);
            return false;
        }
    }

    private void RestartHistoryMaterialization() =>
        Dispatcher.UIThread.PostOnDemand(() => RestartHistoryMaterializationCore());

    private void RestartHistoryMaterializationCore(bool resetRawHistory = false, bool preserveVisibleItems = false)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_isClosing) return;

        CancellationTokenSource previousCancellation;
        bool hasMoreItems;
        lock (_historyStateLock)
        {
            _historyGeneration++;
            previousCancellation = _historyGenerationCancellation;
            _historyGenerationCancellation = new CancellationTokenSource();

            if (resetRawHistory)
            {
                _rawHistoryIds.Clear();
                _historyScanIds.Clear();
                _storageCursor = null;
                _rawHistoryExhausted = false;
            }

            IncludeCurrentHistoryCandidate();
            _historyScanIndex = 0;
            if (!preserveVisibleItems)
            {
                _materializedHistoryIds.Clear();
            }
            hasMoreItems = _historyScanIds.Count > 0 || !_rawHistoryExhausted;
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        if (!preserveVisibleItems)
        {
            _materializedHistorySource.Clear();
            if (_normalizedHistorySearchQuery is null && _current is { } current)
            {
                AddRecentHistoryMetadata(current.Data.Metadata);
            }
        }
        HasMoreItems = hasMoreItems;

        // Wake the loader even when HasMoreItems stayed true. Query changes clear results;
        // pinning changes retain the visible objects while restarting the raw ordering.
        OnPropertyChanged(nameof(HasMoreItems));
    }

    private void AddRecentHistoryMetadata(ChatMetadata metadata) =>
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isClosing || !_metadataMap.ContainsKey(metadata.Id)) return;
            if (!MatchesHistoryView(metadata))
            {
                return;
            }

            if (_normalizedHistorySearchQuery is not null)
            {
                RestartHistoryMaterializationCore(resetRawHistory: true);
                return;
            }

            lock (_historyStateLock)
            {
                if (!MatchesHistoryView(metadata) || !_materializedHistoryIds.Add(metadata.Id)) return;
                _materializedHistorySource.Add(metadata);
            }
        });

    private void RemoveHistoryMetadata(ChatMetadata metadata) =>
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isClosing) return;
            lock (_historyStateLock)
            {
                if (!_materializedHistoryIds.Remove(metadata.Id)) return;
                _materializedHistorySource.Remove(metadata);
            }
        });

    private void EndLoadSession()
    {
        Interlocked.Decrement(ref _activeHistorySessions);
        Dispatcher.UIThread.PostOnDemand(UpdateHistoryBusyState);
    }

    private void UpdateHistoryBusyState()
    {
        if (!_isClosing) IsBusy = Volatile.Read(ref _activeHistorySessions) > 0;
    }

    private void ThrowIfHistoryGenerationChanged(int generation, CancellationToken cancellationToken)
    {
        if (generation != Volatile.Read(ref _historyGeneration))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private bool MatchesHistoryView(ChatMetadata metadata)
    {
        return HistoryView switch
        {
            ChatHistoryView.Archived => metadata.IsArchived,
            ChatHistoryView.Temporary => metadata is { IsArchived: false, IsTemporary: true },
            _ => !metadata.IsArchived
        };
    }

    private static ChatHistorySectionKind GetHistorySection(ChatMetadata metadata)
    {
        if (metadata.IsPinned)
        {
            return ChatHistorySectionKind.Pinned;
        }

        return (DateTimeOffset.UtcNow - metadata.DateModified).TotalDays switch
        {
            < 1 => ChatHistorySectionKind.Today,
            < 2 => ChatHistorySectionKind.Yesterday,
            < 7 => ChatHistorySectionKind.LastWeek,
            < 30 => ChatHistorySectionKind.LastMonth,
            < 365 => ChatHistorySectionKind.LastYear,
            _ => ChatHistorySectionKind.Earlier
        };
    }

    private static string? NormalizeHistorySearchQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    private RootChatContext CreateRoot(ChatData data)
    {
        using var scope = _rootsLock.EnterScope();
        ObjectDisposedException.ThrowIf(_isClosing, this);
        var root = new RootChatContext(data, _chatContextStorage);
        RegisterRoot(root);
        return root;
    }

    private RootChatContext? FindRoot(ChatMetadata metadata)
    {
        var root = FindRoot(metadata.Id);
        return root is not null && ReferenceEquals(root.Data.Metadata, metadata) ? root : null;
    }

    private RootChatContext? FindRoot(Guid id)
    {
        using var scope = _rootsLock.EnterScope();
        return _roots.GetValueOrDefault(id)?.Target;
    }

    private void RegisterRoot(RootChatContext root)
    {
        var data = root.Data;
        root.Idle += OnRootIdle;
        root.Input.Changed += HandleInputChanged;
        _roots[data.Metadata.Id] = root.Reference;
        _metadataMap.TryAdd(data.Metadata.Id, data.Metadata);
        if (data.PersistenceDirtyState.HasChanges || data.Metadata.CaptureChanges().HasChanges)
        {
            QueueSave(data.Metadata);
        }
    }

    private void QueueLeaveUnavailableChat(Guid chatId)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_isClosing && _current?.Data.Metadata.Id == chatId && _currentSwitchCancellation is null)
            {
                LoadRecentAsCurrentAsync().Detach(_logger.ToExceptionHandler());
            }
        });
    }

    private void OnRootIdle(object? sender, EventArgs args)
    {
        // Short draft edits also raise Idle, but have no durable history to synchronize.
        if (!_isClosing && sender is RootChatContext root && CanSave(root.Data.Metadata, root))
        {
            CompleteStableSaveAsync().Detach(_logger.ToExceptionHandler());
        }
    }

    private async Task CompleteStableSaveAsync()
    {
        await FlushSyncSavesAsync().ConfigureAwait(false);
        if (!_isClosing)
        {
            // Streaming may already have been saved while busy; idle still releases its pending sync work.
            SyncRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Releases synchronous callers through the same shutdown path as the DI async lifetime.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Stops every root before final persistence. Final save failures are logged; disposal never
    /// waits for the UI dispatcher, which may already have exited before DI starts its teardown.
    /// The injected storage factory can finish writes without resolving services from the root provider.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var scope = _rootsLock.EnterScope();
        if (_disposeTask is null)
        {
            _isClosing = true;
            var roots = _roots.Values.Select(reference => reference.Target).OfType<RootChatContext>().ToArray();
            var loads = _loads.Keys.ToArray();
            var cancellations = _loads.Values.Select(pending => pending.Cancellation.CancelAsync()).ToArray();
            _disposeTask = Task.Run(() => DisposeCoreAsync(roots, loads, cancellations));
        }

        return new ValueTask(_disposeTask);
    }

    private async Task DisposeCoreAsync(RootChatContext[] roots, Task<RootChatContext>[] loads, Task[] cancellations)
    {
        _saveDebounceExecutor.Dispose();
        CancelCurrentSwitch();
        await _historyGenerationCancellation.CancelAsync().ConfigureAwait(false);
        var stopping = Task.WhenAll(roots.Select(root => root.StopAsync()));
        try
        {
            await Task.WhenAll(cancellations.Concat(loads)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "A conversation load failed while shutting down.");
        }

        await stopping.ConfigureAwait(false);

        await _commitGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CommitPendingSavesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save final chat changes during shutdown.");
        }
        finally
        {
            _isDisposed = true;
            _commitGate.Release();
            WeakReferenceMessenger.Default.UnregisterAll(this);
            foreach (var root in roots)
            {
                await root.DisposeAsync().ConfigureAwait(false);
            }

            _roots.Clear();
            _historyGenerationCancellation.Dispose();
            _historyConnection.Dispose();
            _materializedHistorySource.Dispose();
            _historyRegrouper.Dispose();
            // Existing viewport callers may still release this asynchronous gate after cancellation.
            // It owns no native wait handle and is collected with the manager.
        }
    }

    private sealed record PendingSave(RootChatContext? Root, ChatMetadata Metadata, string? LocalAccountId);

    private sealed record PendingInput(RootChatContext Root, ChatInputWrite Write);

    private sealed record PendingContextLoad(Guid ChatId, CancellationTokenSource Cancellation)
    {
        // A remote commit superseded this detached read; retry before publishing a canonical root.
        public bool ShouldReload { get; set; }
    }

    private sealed class IncrementalLoadSession(ChatContextManager owner) : IIncrementalLoadSession
    {
        private int _isDisposed;

        public ValueTask<IncrementalLoadResult> LoadMoreAsync(
            int count,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            return owner.LoadMoreHistoryAsync(count, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            owner.EndLoadSession();
        }
    }

    private static bool IsEmptyDraft([NotNullWhen(true)] RootChatContext? chatContext)
    {
        // Accepted work can still be queuing its first user node. It is not a reusable draft.
        return chatContext is { HasWork: false, Data: { HasHistory: false, Metadata.IsPersisted: false } };
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex WorkingDirectoryRegex();
}

public static class ChatContextManagerExtensions
{
    public static IServiceCollection AddChatContextManager(this IServiceCollection services)
    {
        services.AddSingleton<ChatContextManager>();
        services.AddSingleton<IChatContextManager>(x => x.GetRequiredService<ChatContextManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatContextManager>());
        return services;
    }
}
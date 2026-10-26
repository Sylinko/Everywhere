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
    IAsyncInitializer,
    IRecipient<ChatContextMetadataChangedMessage>,
    IDisposable,
    IAsyncDisposable
{
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
    public IReadOnlyBindableList<ChatDataHistoryGroup> AllHistory { get; }

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
    public IRelayCommand CreateNewCommand => field ??= new RelayCommand(CreateNew, () => !IsEmptyContext(_current));

    /// <inheritdoc />
    public bool IsArchiveView
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

    private RootChatContext? _current;
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
    /// A buffer for chat contexts and their metadata to be saved.
    /// Unopened conversations need only metadata writes. Open roots stay strongly held through save retries.
    /// </summary>
    private readonly Dictionary<Guid, PendingSave> _saveBuffer = [];
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private readonly Settings _settings;
    private readonly IChatContextStorage _chatContextStorage;
    private readonly ILogger<ChatContextManager> _logger;
    private readonly DebounceExecutor<ChatContextManager, ThreadingTimerImpl> _saveDebounceExecutor;

    private CancellationTokenSource _historyGenerationCancellation = new();
    private string? _normalizedHistorySearchQuery;
    private Guid? _storageCursor;
    private int _historyScanIndex;
    private int _historyGeneration;
    private int _activeHistorySessions;
    private bool _rawHistoryExhausted;
    private readonly Lock _rootsLock = new();
    private readonly Dictionary<Guid, ResilientReference<RootChatContext>> _roots = [];
    private readonly Dictionary<Task<RootChatContext>, CancellationTokenSource> _loads = [];
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
            .GroupOn(GetHumanizedDate, _historyRegrouper)
            .Transform(static group => new ChatDataHistoryGroup(group))
            .DisposeMany()
            .Sort(SortExpressionComparer<ChatDataHistoryGroup>.Ascending(static history => history.Date))
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

        WeakReferenceMessenger.Default.Register(this);

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
            // Running roots remain observable after a temporary history entry is dismissed.
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
                if (!_isClosing && !message.Metadata.States.HasFlag(ChatContextMetadataStates.Busy))
                {
                    // Runtime completion persists independently of whether a view processes its notification.
                    FlushPendingSavesAsync().Detach(_logger.ToExceptionHandler());
                }

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
            case nameof(ChatMetadata.ArchivedAt):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    if (!_metadataMap.ContainsKey(message.Metadata.Id))
                    {
                        return;
                    }

                    RemoveHistoryMetadata(message.Metadata);
                    AddRecentHistoryMetadata(message.Metadata);
                });
                break;
            }
            case nameof(ChatMetadata.DateModified):
            case nameof(ChatMetadata.Topic):
            {
                QueueSave(message);
                if (!_isClosing)
                {
                    _saveDebounceExecutor.Trigger();
                }

                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (_isClosing) return;
                    CreateNewCommand.NotifyCanExecuteChanged();
                    HandleHistoryMetadataChanged(
                        message.Metadata,
                        message.PropertyName == nameof(ChatMetadata.DateModified));
                });
                break;
            }
        }
    }

    /// <inheritdoc />
    public Task FlushPendingSavesAsync()
    {
        // Serialization and SQLite work must not run synchronously on the UI caller. The gate
        // includes batch capture so a final flush also waits for any earlier in-flight write.
        return Task.Run(FlushPendingSavesCoreAsync);
    }

    private async Task FlushPendingSavesCoreAsync()
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isClosing) return;
            await SaveBufferedMessagesAsync().ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task SaveBufferedMessagesAsync()
    {
        // The caller holds _saveGate. Shutdown uses this same batch path after draining roots.
        PendingSave[] messages;
        lock (_saveBuffer)
        {
            messages = [.. _saveBuffer.Values];
            _saveBuffer.Clear();
        }

        var failures = new List<Exception>();
        foreach (var message in messages)
        {
            try
            {
                await SavePendingMessageAsync(message).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Failed to save conversation {message.Metadata.Id}.", exception));
            }
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("Some conversations could not be saved.", failures);
        }
    }

    private async Task SavePendingMessageAsync(PendingSave message)
    {
        if (IsEmptyContext(message.Root) || message.Metadata.IsTemporary)
        {
            return;
        }

        try
        {
            if (message.Root is { } context)
            {
                await SaveDataAsync(context).ConfigureAwait(false);
            }
            else
            {
                var metadata = ChatMetadataWrite.Capture(message.Metadata);
                await _chatContextStorage.SaveChatContextMetadataAsync(metadata).ConfigureAwait(false);
            }
        }
        catch
        {
            // Preserve the strong reference and merge changes received during the failed write.
            QueueSave(message);
            throw;
        }
    }

    private async Task SaveDataAsync(RootChatContext context)
    {
        // The save-buffer entry and this frame retain the data/runtime until both commit and
        // acknowledgement finish. Failure returns that same ownership to the pending queue.
        var data = context.Data;
        var (batch, changes) = await context.DispatchAsync(() => ChatWriteBatch.Capture(data)).ConfigureAwait(false);
        await _chatContextStorage.SaveChatContextAsync(batch).ConfigureAwait(false);
        await context.DispatchAsync(() => data.PersistenceDirtyState.Acknowledge(changes)).ConfigureAwait(false);
    }

    private async Task SavePendingChatAsync(Guid id)
    {
        // The caller holds _saveGate, just as the full flush does when taking its batch.
        PendingSave? message;
        lock (_saveBuffer)
        {
            _saveBuffer.Remove(id, out message);
        }
        if (message is not null)
        {
            await SavePendingMessageAsync(message);
        }
    }

    private void QueueSave(ChatContextMetadataChangedMessage message)
    {
        // Capture the owner at notification ingress, while the writer still holds it. A pending
        // or failed save must not rely on a later lookup through the manager's weak index.
        QueueSave(new PendingSave(FindRoot(message.Metadata), message.Metadata));
    }

    private void QueueSave(PendingSave entry)
    {
        lock (_saveBuffer)
        {
            if (!_metadataMap.TryGetValue(entry.Metadata.Id, out var metadata) ||
                !ReferenceEquals(metadata, entry.Metadata))
            {
                return;
            }

            if (_saveBuffer.TryGetValue(entry.Metadata.Id, out var pending))
            {
                _saveBuffer[entry.Metadata.Id] = pending.Root is not null ? pending : entry;
            }
            else
            {
                _saveBuffer.Add(entry.Metadata.Id, entry);
            }
        }
    }

    /// <summary>
    /// Delete all directories in _runtimeConstantProvider.EnsureWritableDataFolderPath($"plugins") that named with date (yyyy-MM-dd)
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
        if (IsEmptyContext(_current)) return;
        CancelCurrentSwitch();

        var isCurrentTemporary = _current?.Data.Metadata.IsTemporary is true;
        if (isCurrentTemporary)
        {
            // Remove the temporary chat data before creating a new one
            // Temporary chat contexts are not saved to storage, so no need to delete from storage.
            if (_metadataMap.Remove(_current!.Data.Metadata.Id, out var removed))
            {
                RemoveHistoryMetadata(removed);
            }
        }

        var data = new ChatData
        {
            Metadata =
            {
                IsTemporary = _settings.ChatWindow.TemporaryChatMode switch
                {
                    TemporaryChatMode.RememberLast => isCurrentTemporary,
                    TemporaryChatMode.Always => true,
                    _ => false
                },
            },
        };
        _current = CreateRoot(data);
        // After created, the chat data is not added to the storage yet.
        // It will be added when it's property has changed.

        AddRecentHistoryMetadata(_current.Data.Metadata);
        NotifyCurrentChanged();
    }

    /// <inheritdoc />
    public async Task RenameAsync(ChatMetadata metadata, string? topic)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
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
                metadata.Topic = topic;
            }
            else
            {
                await root.ApplyAsync(() => metadata.Topic = topic).ConfigureAwait(false);
            }
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ArchiveAsync(ChatMetadata metadata)
    {
        if (metadata.IsTemporary)
        {
            throw new InvalidOperationException("Temporary conversations cannot be archived.");
        }

        await Task.Run(async () =>
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_isClosing, this);
                await SavePendingChatAsync(metadata.Id).ConfigureAwait(false);
                if (_current is { Data.HasHistory: false } empty && empty.Data.Metadata.Id == metadata.Id)
                {
                    await SaveDataAsync(empty).ConfigureAwait(false);
                }

                // Commit model state before releasing the persistence gate. History projection
                // observes the notification independently; it is not part of this transaction.
                var archivedAt = await _chatContextStorage.ArchiveAsync(metadata.Id).ConfigureAwait(false);
                await PublishArchiveStateAsync(metadata, archivedAt).ConfigureAwait(false);
            }
            finally
            {
                _saveGate.Release();
            }
        });
        if (_current?.Data.Metadata.Id == metadata.Id)
        {
            await LoadRecentAsCurrentAsync();
        }
    }

    /// <inheritdoc />
    public async Task RestoreAsync(ChatMetadata metadata)
    {
        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            await _chatContextStorage.RestoreAsync(metadata.Id).ConfigureAwait(false);
            await PublishArchiveStateAsync(metadata, null).ConfigureAwait(false);
        }
        finally
        {
            _saveGate.Release();
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
    public async Task DeletePermanentlyAsync(ChatMetadata metadata)
    {
        await Task.Run(async () =>
        {
            await _saveGate.WaitAsync().ConfigureAwait(false);
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
                    await _chatContextStorage.DeletePermanentlyAsync(metadata.Id).ConfigureAwait(false);
                }
                catch
                {
                    using (_rootsLock.EnterScope())
                    {
                        if (!_isClosing)
                        {
                            root?.ResumeAfterFailedDeletion();
                        }
                    }

                    throw;
                }

                // Only a failed database deletion may reopen admission. Once committed, resource
                // cleanup cannot make this conversation writable again, even if cleanup fails.
                CancellationTokenSource historyCancellation;
                lock (_historyStateLock)
                {
                    _historyGeneration++;
                    historyCancellation = _historyGenerationCancellation;
                    lock (_saveBuffer)
                    {
                        _metadataMap.TryRemove(metadata.Id, out _);
                        _saveBuffer.Remove(metadata.Id);
                    }
                }

                await historyCancellation.CancelAsync().ConfigureAwait(false);
                RestartHistoryMaterialization();
                if (root is not null)
                {
                    await root.DisposeAsync().ConfigureAwait(false);
                    using (_rootsLock.EnterScope())
                    {
                        _roots.Remove(metadata.Id);
                    }
                }
            }
            finally
            {
                _saveGate.Release();
            }
        });

        RemoveHistoryMetadata(metadata);
        if (_current?.Data.Metadata.Id == metadata.Id)
        {
            await LoadRecentAsCurrentAsync();
        }
    }

    /// <inheritdoc />
    public async Task DismissTemporaryAsync(ChatMetadata metadata)
    {
        if (!metadata.IsTemporary)
        {
            throw new InvalidOperationException("Only temporary conversations can be dismissed without archiving.");
        }

        _metadataMap.TryRemove(metadata.Id, out _);
        RemoveHistoryMetadata(metadata);
        if (_current?.Data.Metadata.Id == metadata.Id)
        {
            await LoadRecentAsCurrentAsync();
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
            var historyItem = LoadedMetadata.Where(metadata => !metadata.IsArchived)
                .OrderByDescending(metadata => metadata.DateModified).ThenByDescending(metadata => metadata.Id)
                .FirstOrDefault();
            await foreach (var candidate in _chatContextStorage.QueryChatContextsAsync(1, cancellationToken: cancellation.Token))
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
        if (ReferenceEquals(_currentSwitchCancellation, cancellation))
        {
            _currentSwitchCancellation = null;
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
                    !ReferenceEquals(_currentSwitchCancellation, cancellation))
                {
                    return;
                }

                if (next is null)
                {
                    _current = null;
                    CreateNew();
                }
                else
                {
                    _current = next;
                    NotifyCurrentChanged();
                }

                // Preserve the delayed collection cleanup, but never remove a chat selected again
                // before this callback runs.
                Dispatcher.UIThread.Post(
                    () =>
                    {
                        if (!_isClosing && previous is not null && previous.Data.Metadata.Id != _current?.Data.Metadata.Id &&
                            (IsEmptyContext(previous) || previous.Data.Metadata.IsTemporary) &&
                            _metadataMap.Remove(previous.Data.Metadata.Id, out _))
                        {
                            RemoveHistoryMetadata(previous.Data.Metadata);
                        }
                    },
                    DispatcherPriority.Background);
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
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            loading = Task.Run(() => LoadContextCoreAsync(metadata, cancellation.Token), CancellationToken.None);
            _loads.Add(loading, cancellation);
        }

        try
        {
            return await loading.ConfigureAwait(false);
        }
        finally
        {
            using var scope = _rootsLock.EnterScope();
            if (_loads.Remove(loading, out var cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task<RootChatContext> LoadContextCoreAsync(ChatMetadata metadata, CancellationToken cancellationToken)
    {
        var data = await _chatContextStorage.GetChatContextAsync(metadata, cancellationToken).ConfigureAwait(false);
        try
        {
            await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var scope = _rootsLock.EnterScope();
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_isClosing, this);
                if (!_metadataMap.ContainsKey(metadata.Id))
                {
                    throw new OperationCanceledException("The conversation was removed while loading.");
                }

                // Concurrent readers may have read the same topology, but only one graph becomes
                // canonical. A late read never replaces nodes already used by another window.
                if (_roots.GetValueOrDefault(metadata.Id)?.Target is { } existing)
                {
                    if (!ReferenceEquals(data, existing.Data))
                    {
                        data.Dispose();
                    }

                    return existing;
                }

                return CreateRoot(data);
            }
            finally
            {
                _saveGate.Release();
            }
        }
        catch
        {
            data.Dispose();
            throw;
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
            if (!_metadataMap.TryGetValue(id, out var metadata) || metadata.IsArchived != IsArchiveView) continue;
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
                        metadata.IsArchived != IsArchiveView ||
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
        Guid? cursor;
        lock (_historyStateLock)
        {
            cursor = _storageCursor;
        }

        var fetched = new List<ChatMetadata>(count);
        await foreach (var metadata in _chatContextStorage.QueryChatContextsAsync(
                           count,
                           cursor,
                           cancellationToken,
                           IsArchiveView).ConfigureAwait(false))
        {
            fetched.Add(metadata);
        }

        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            foreach (var metadata in fetched)
            {
                var canonical = _metadataMap.GetOrAdd(metadata.Id, metadata);

                _storageCursor = metadata.Id;
                if (_rawHistoryIds.Add(canonical.Id) && canonical.IsArchived == IsArchiveView)
                {
                    _historyScanIds.Add(canonical.Id);
                }
            }

            if (fetched.Count < count)
            {
                _rawHistoryExhausted = true;
            }
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

    private void RestartHistoryMaterializationCore(bool resetRawHistory = false)
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
                if (_current is not null)
                {
                    _rawHistoryIds.Add(_current.Data.Metadata.Id);
                }

                _storageCursor = null;
                _rawHistoryExhausted = false;
            }

            _historyScanIds.Clear();
            _historyScanIds.AddRange(
                _metadataMap.Values
                    .Where(metadata => metadata.IsArchived == IsArchiveView)
                    .OrderByDescending(static metadata => metadata.DateModified)
                    .ThenByDescending(static metadata => metadata.Id)
                    .Select(static metadata => metadata.Id));
            _historyScanIndex = 0;
            _materializedHistoryIds.Clear();
            hasMoreItems = _historyScanIds.Count > 0 || !_rawHistoryExhausted;
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        _materializedHistorySource.Clear();
        HasMoreItems = hasMoreItems;

        // A reset must wake the behavior even when the boolean value stayed true. The ScrollViewer
        // also returns to the top because the materialized result was cleared.
        OnPropertyChanged(nameof(HasMoreItems));
    }

    private void AddRecentHistoryMetadata(ChatMetadata metadata) =>
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_isClosing) return;
            if (metadata.IsArchived != IsArchiveView)
            {
                return;
            }

            lock (_historyStateLock)
            {
                _rawHistoryIds.Add(metadata.Id);
            }

            if (_normalizedHistorySearchQuery is not null)
            {
                RestartHistoryMaterializationCore();
                return;
            }

            lock (_historyStateLock)
            {
                if (metadata.IsArchived != IsArchiveView || !_materializedHistoryIds.Add(metadata.Id)) return;
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

    private void HandleHistoryMetadataChanged(ChatMetadata metadata, bool invalidatesStorageCursor)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (invalidatesStorageCursor)
        {
            lock (_historyStateLock)
            {
                // A modified cursor is no longer a valid boundary in UpdatedAt order. Restarting the
                // raw cursor is cheap because already-known IDs are deduplicated before entering the
                // scan, and it prevents a selected old conversation from making intermediate rows skip.
                _storageCursor = null;
                _rawHistoryExhausted = false;
            }
        }

        if (_normalizedHistorySearchQuery is not null)
        {
            RestartHistoryMaterializationCore();
            return;
        }

        lock (_historyStateLock)
        {
            if (!_materializedHistoryIds.Contains(metadata.Id)) return;
        }

        _historyRegrouper.OnNext(Unit.Default);
    }

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

    private static HumanizedDate GetHumanizedDate(ChatMetadata metadata) =>
        (DateTimeOffset.UtcNow - metadata.DateModified).TotalDays switch
        {
            < 1 => HumanizedDate.Today,
            < 2 => HumanizedDate.Yesterday,
            < 7 => HumanizedDate.LastWeek,
            < 30 => HumanizedDate.LastMonth,
            < 365 => HumanizedDate.LastYear,
            _ => HumanizedDate.Earlier
        };

    private static string? NormalizeHistorySearchQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    /// <summary>
    /// Defers history I/O until the history viewport requests its first page.
    /// </summary>
    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

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
        using var scope = _rootsLock.EnterScope();
        var root = _roots.GetValueOrDefault(metadata.Id)?.Target;
        return root is not null && ReferenceEquals(root.Data.Metadata, metadata) ? root : null;
    }

    private void RegisterRoot(RootChatContext root)
    {
        var data = root.Data;
        _roots[data.Metadata.Id] = root.Reference;
        _metadataMap.TryAdd(data.Metadata.Id, data.Metadata);
        if (data.PersistenceDirtyState.HasChanges)
        {
            QueueSave(new ChatContextMetadataChangedMessage(data.Metadata, nameof(ChatMetadata.DateModified)));
        }
    }

    /// <summary>
    /// Releases synchronous callers through the same shutdown path as the DI async lifetime.
    /// </summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    /// <summary>
    /// Stops every root before final persistence. Final save failures are logged; disposal never
    /// waits for the UI dispatcher, which may already have exited before DI starts its teardown.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        using var scope = _rootsLock.EnterScope();
        if (_disposeTask is null)
        {
            _isClosing = true;
            var roots = _roots.Values.Select(reference => reference.Target).OfType<RootChatContext>().ToArray();
            var loads = _loads.Keys.ToArray();
            var cancellations = _loads.Values.Select(cancellation => cancellation.CancelAsync()).ToArray();
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

        await _saveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveBufferedMessagesAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to save final chat changes during shutdown.");
        }
        finally
        {
            _isDisposed = true;
            _saveGate.Release();
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

    private sealed record PendingSave(RootChatContext? Root, ChatMetadata Metadata);

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

    private static bool IsEmptyContext([NotNullWhen(true)] RootChatContext? chatContext) => chatContext is { Data.HasHistory: false };

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
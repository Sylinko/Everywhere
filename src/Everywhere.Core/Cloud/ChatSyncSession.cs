using System.Threading.Channels;
using Everywhere.Chat;
using Everywhere.Database;
using Everywhere.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

/// <summary>
/// One enabled account's synchronization lifetime. Owns coherent inventory publications,
/// coalesced passes and per-chat admission shared by transfers and participation commands.
/// Network waits never hold an account-wide management gate or a SQLite transaction.
/// </summary>
public sealed partial class ChatSyncSession : IAsyncDisposable
{
    /// <summary>
    /// Authenticated subject for all requests and durable records in this lifetime.
    /// </summary>
    public string AccountId => _transport.AccountId;

    /// <summary>
    /// Canceled when this account is disabled, replaced or disposed.
    /// </summary>
    public CancellationToken Token => _lifetime.Token;

    /// <summary>
    /// Latest coherent publication; safe to read from any thread.
    /// </summary>
    public ChatSyncSessionState State => Volatile.Read(ref _state);

    /// <summary>
    /// State changed. Observers marshal once at their UI boundary and read the latest State.
    /// </summary>
    public event EventHandler? Changed;

    private const int MaximumConcurrentChats = 2;
    private readonly IDbContextFactory<ChatV2DbContext> _dbFactory;
    private readonly ChatTurnStorage _storage;
    private readonly ChatContextManager _manager;
    private readonly ChatSyncTransport _transport;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    // Serialize local reads with their publications, not network I/O. A late SQLite
    // projection cannot publish after a newer projection and reverse a completed command.
    private readonly SemaphoreSlim _projectionGate = new(1, 1);
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private readonly Task[] _workers;
    private readonly Dictionary<Guid, ChatWork> _work = [];
    private readonly Dictionary<Guid, ChatSyncItem> _items = [];
    private readonly Dictionary<Guid, ChatSyncCopy> _copies = [];
    private readonly Dictionary<Guid, Exception> _errors = [];

    private ChatSyncSessionState _state = new(null, false, null);
    private ChatSyncStorageUsage? _usage;
    private long _usageRevision;
    private Task? _initialization;
    private TaskCompletionSource? _synchronization;
    private bool _needsAnotherPass;
    private bool _shouldRetryQuota;
    private Guid? _priorityChat;

    /// <summary>
    /// Creates a session over an authenticated transport. The caller owns its HttpClient
    /// and disposes it after this session has canceled and drained its outstanding work.
    /// </summary>
    public ChatSyncSession(
        IDbContextFactory<ChatV2DbContext> dbFactory,
        ChatTurnStorage storage,
        ChatContextManager manager,
        ChatSyncTransport transport,
        ILogger logger)
    {
        _dbFactory = dbFactory;
        _storage = storage;
        _manager = manager;
        _transport = transport;
        _logger = logger;
        _workers = ValueEnumerable.Range(0, MaximumConcurrentChats).Select(_ => RunWorkerAsync()).ToArray();
    }

    /// <summary>
    /// Coalesces a saved-history signal into this pass or one following pass.
    /// </summary>
    public void RequestSync()
    {
        RequestPass(false, true).Detach(_logger.ToExceptionHandler());
    }

    /// <summary>
    /// Runs or joins a pass and retries quota-blocked work. Cancellation only stops this caller's
    /// wait; the account session owns the pass, including saved-history signals received meanwhile.
    /// </summary>
    public Task SynchronizeAsync(bool shouldRetryQuota = true, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return RequestPass(shouldRetryQuota, false).WaitAsync(cancellationToken);
    }

    private Task RequestPass(bool shouldRetryQuota, bool hasNewChanges)
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_lifetime.IsCancellationRequested)
            {
                return Task.FromCanceled(_lifetime.Token);
            }

            _shouldRetryQuota |= shouldRetryQuota;
            if (_synchronization is { } running)
            {
                _needsAnotherPass |= hasNewChanges || shouldRetryQuota;
                return running.Task;
            }

            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _synchronization = completion;
            _state = _state with { IsSynchronizing = true };
        }

        Changed?.Invoke(this, EventArgs.Empty);
        RunPassesAsync(completion).Detach(_logger.ToExceptionHandler());
        return completion.Task;
    }

    private async Task EnsureInitializedAsync(CancellationToken token)
    {
        Task initialization;
        lock (_gate)
        {
            if (_initialization is null || _initialization.IsFaulted || _initialization.IsCanceled)
            {
                _initialization = InitializeCoreAsync();
            }
            initialization = _initialization;
        }
        await initialization.WaitAsync(token).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync()
    {
        var token = _lifetime.Token;
        try
        {
            if (_storage.SyncAccountId != AccountId)
            {
                await _manager.FlushPendingSavesAsync().ConfigureAwait(false);
            }
            await using (var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
                await new ChatSyncStateStore(db).EnrollAsync(AccountId, _transport.Compatibility.SyncGeneration, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }

            // No await between this check and binding: retirement cancels this lifetime
            // before another account may claim queued writes.
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                _storage.SyncAccountId = AccountId;
            }

            await DiscoverAsync(token).ConfigureAwait(false);
            var copies = new List<ChatSyncCopy>();
            ChatSyncStoragePage page;
            string? cursor = null;
            do
            {
                page = await _transport.ListCopiesAsync(cursor, token).ConfigureAwait(false);
                copies.AddRange(page.Chats);
                cursor = page.Cursor;
            }
            while (cursor is not null);

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                foreach (var copy in copies)
                {
                    _copies[Guid.ParseExact(copy.ChatId, "D")] = copy;
                }
                _usage ??= page.Usage;
                _state = _state with { Error = null };
            }

            await ReloadItemsAsync(null, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            SetError(error);
            throw;
        }
    }

    private async Task RunPassesAsync(TaskCompletionSource completion)
    {
        var token = _lifetime.Token;
        Exception? failure = null;
        try
        {
            await EnsureInitializedAsync(token).ConfigureAwait(false);
            while (true)
            {
                bool shouldRetry;
                lock (_gate)
                {
                    _needsAnotherPass = false;
                    shouldRetry = _shouldRetryQuota;
                    _shouldRetryQuota = false;
                }

                await SynchronizeCoreAsync(shouldRetry, token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_needsAnotherPass)
                    {
                        continue;
                    }

                    // Release ownership in the same critical section as the final
                    // request check. A new save either extends this pass or starts
                    // the next one; it cannot disappear between check and teardown.
                    _synchronization = null;
                    _state = _state with { IsSynchronizing = false };
                    break;
                }
            }
        }
        catch (Exception error)
        {
            failure = error;
            if (error is not OperationCanceledException)
            {
                SetError(error);
            }
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_synchronization, completion))
                {
                    _synchronization = null;
                    _state = _state with { IsSynchronizing = false };
                }
            }
            PublishInventory();
        }
        if (failure is OperationCanceledException canceled)
        {
            completion.TrySetCanceled(canceled.CancellationToken);
        }
        else if (failure is not null)
        {
            completion.TrySetException(failure);
        }
        else
        {
            completion.TrySetResult();
        }
    }

    private async Task DiscoverAsync(CancellationToken token)
    {
        var transfer = new ChatSyncTransfer(_dbFactory, _transport);
        ChatSyncDiscovery page;
        do
        {
            page = await transfer.DiscoverAsync(token).ConfigureAwait(false);
            foreach (var head in page.Heads)
            {
                if (head.Availability != ChatSyncAvailability.Available && head.Lifecycle != ChatSyncLifecycle.Deleted)
                {
                    var id = Guid.ParseExact(head.ChatId, "D");
                    var work = GetWork(id);
                    await work.Gate.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        await _manager.CommitClearedSyncHeadAsync(AccountId, head, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        work.Gate.Release();
                    }
                }
                UpdateCopy(head);
            }
            if (page.Usage is { } usage)
            {
                RecordUsage(page.Cursor, usage);
            }
        }
        while (page.HasMore);
        await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        await ChatTurnStorage.EnrollLocalHistoryAsync(db, AccountId, token).ConfigureAwait(false);
    }

    // ReSharper disable AccessToDisposedClosure
    private async Task SynchronizeCoreAsync(bool shouldRetryQuota, CancellationToken token)
    {
        // Sync only committed local history. A failed flush remains queued by the manager and
        // fails this pass, rather than reporting success against an older database version.
        await _manager.FlushSyncSavesAsync().ConfigureAwait(false);

        await DiscoverAsync(token).ConfigureAwait(false);
        await ReloadItemsAsync(null, token).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
        var ids = PendingChatIds(db);
        var ordered = await ids
            .Select(id => new
            {
                Id = id,
                UpdatedAt = db.Chats
                    .Where(chat => chat.Id == id)
                    .Select(chat => (DateTimeOffset?)chat.UpdatedAt)
                    .FirstOrDefault(),
                Revision = db.SyncChats
                    .Where(chat => chat.AccountId == AccountId && chat.ChatId == id).Select(chat => chat.DiscoveredRevision)
                    .First()
            })
            .OrderByDescending(row => row.UpdatedAt)
            .ThenByDescending(row => row.UpdatedAt.HasValue ? 0 : row.Revision)
            .ThenByDescending(row => row.Id)
            .Select(row => row.Id)
            .ToArrayAsync(token)
            .ConfigureAwait(false);

        Guid? priority;
        lock (_gate)
        {
            priority = _priorityChat;
            _priorityChat = null;
        }

        if (priority is { } first && ordered.Contains(first))
        {
            await QueueChat(first, shouldRetryQuota, token).WaitAsync(token).ConfigureAwait(false);
            ordered = ordered.AsValueEnumerable().Where(id => id != first).ToArray();
        }

        var jobs = ordered.AsValueEnumerable().Select(id => QueueChat(id, shouldRetryQuota, token)).ToArray();
        await Task.WhenAll(jobs).WaitAsync(token).ConfigureAwait(false);

        lock (_gate)
        {
            _state = _state with { Error = null };
        }

        PublishInventory();
    }

    private void RecordUsage(long revision, ChatSyncStorageUsage usage)
    {
        lock (_gate)
        {
            if (revision >= _usageRevision)
            {
                _usageRevision = revision;
                _usage = usage;
            }
        }
    }

    private void SetError(Exception error)
    {
        lock (_gate)
        {
            _state = _state with { Error = error };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private ChatWork GetWork(Guid id)
    {
        lock (_gate)
        {
            if (!_work.TryGetValue(id, out var work))
            {
                work = new ChatWork();
                _work.Add(id, work);
            }

            return work;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Task cancellation;
        lock (_gate)
        {
            // Share the initialization binding boundary: retirement either follows that
            // binding or cancels it first, never lets an old account bind after replacement.
            cancellation = _lifetime.CancelAsync();
        }

        await cancellation.ConfigureAwait(false);
        Task[] tasks;
        lock (_gate)
        {
            foreach (var work in _work.Values)
            {
                work.Queued?.TrySetCanceled(_lifetime.Token);
            }
            _queue.Writer.TryComplete();
            tasks = _work.Values
                .AsValueEnumerable()
                .Select(work => work.Management)
                .OfType<Task>()
                .Concat(new[] { _initialization, _synchronization?.Task }.AsValueEnumerable().OfType<Task>())
                .Concat(_workers)
                .ToArray();
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (error is not OperationCanceledException)
            {
                _logger.LogWarning(error, "Sync session {AccountId} finished with an error.", AccountId);
            }
        }
        // Keep the canceled token readable by already queued UI notifications.
    }

    private sealed class ChatWork
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public CancellationTokenSource? Transfer { get; set; }
        public Task? Management { get; set; }
        public ChatSyncOperation Operation { get; set; }
        public TaskCompletionSource? Queued { get; set; }
        public CancellationToken QueueToken { get; set; }
        public bool ShouldRetryQuota { get; set; }
        public bool ShouldRunAgain { get; set; }
    }
}
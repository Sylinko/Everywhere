using Everywhere.Storage;
using MessagePack;
using Microsoft.EntityFrameworkCore;

namespace Everywhere.Cloud;

partial class ChatSyncSession
{
    private async Task ReloadItemsAsync(Guid? chatId, CancellationToken token)
    {
        await _projectionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Dictionary<Guid, ChatSyncCopy> copies;
            Dictionary<Guid, Exception> errors;
            lock (_gate)
            {
                copies = _copies.Where(pair => !chatId.HasValue || pair.Key == chatId).ToDictionary();
                errors = new Dictionary<Guid, Exception>(_errors);
            }

            await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var local = await new ChatSyncStateStore(db)
                .GetEligibleLocalChats(AccountId)
                .Where(chat => !chatId.HasValue || chat.Id == chatId)
                .Select(chat => new { chat.Id, chat.Topic, chat.UpdatedAt, chat.IsSyncExcluded })
                .ToDictionaryAsync(chat => chat.Id, token)
                .ConfigureAwait(false);
            var states = await db.SyncChats
                .AsNoTracking()
                .Where(row => row.AccountId == AccountId && (!chatId.HasValue || row.ChatId == chatId))
                .ToDictionaryAsync(row => row.ChatId, token)
                .ConfigureAwait(false);
            var pendingQuery = db.SyncPending
                .Where(row => row.AccountId == AccountId && (!chatId.HasValue || row.ChatId == chatId));
            var pending = await pendingQuery
                .Select(row => row.ChatId).Distinct()
                .ToHashSetAsync(token)
                .ConfigureAwait(false);
            var restarting = await pendingQuery
                .Where(row => row.Kind == ChatSyncRecordKind.Lifecycle && row.Key == "republish")
                .Select(row => row.ChatId)
                .ToHashSetAsync(token)
                .ConfigureAwait(false);
            var submissions = await db.SyncSubmissions
                .Where(row => row.AccountId == AccountId && (!chatId.HasValue || row.ChatId == chatId))
                .Select(row => new { row.ChatId, row.QuotaRejection })
                .ToDictionaryAsync(row => row.ChatId, token)
                .ConfigureAwait(false);
            var received = await db.SyncRemote
                .Where(row => row.AccountId == AccountId && (!chatId.HasValue || row.ChatId == chatId))
                .GroupBy(row => row.ChatId)
                .Select(group => new { ChatId = group.Key, Revision = group.Max(row => row.Revision) })
                .ToDictionaryAsync(row => row.ChatId, row => row.Revision, token)
                .ConfigureAwait(false);

            var items = new List<ChatSyncItem>();
            var resolvedErrors = new List<Guid>();
            foreach (var id in local.Keys.AsValueEnumerable().Union(copies.Keys).Union(states.Keys))
            {
                var state = states.GetValueOrDefault(id);
                var head = state?.ReadAppliedHead();
                var copy = copies.GetValueOrDefault(id);
                var hasLocal = local.TryGetValue(id, out var chat);
                // Installed stops are authoritative over stale HTTP previews. A genuinely
                // newer available head is a later explicit republish, not the old copy.
                if (head is { Availability: not ChatSyncAvailability.Available } && (copy is null || copy.Revision <= head.Revision))
                {
                    copy = null;
                }
                if (head is { Availability: ChatSyncAvailability.Available, Revision: > 0, Lifecycle: not ChatSyncLifecycle.Deleted } &&
                    (copy is null || head.Revision > copy.Revision))
                {
                    copy = new ChatSyncCopy(head.ChatId, chat?.Topic, head.Bytes, head.Revision, head.LifecycleRevision);
                }
                if (head is { Lifecycle: ChatSyncLifecycle.Deleted } && !hasLocal && copy is null)
                {
                    continue;
                }

                var isExcluded = chat?.IsSyncExcluded == true;
                var isCleared = head is { Availability: not ChatSyncAvailability.Available } && copy is null;
                var isParticipating = !isExcluded && (!isCleared || restarting.Contains(id));
                if (!hasLocal && copy is null && !isParticipating)
                {
                    continue;
                }

                if (isExcluded)
                {
                    copy = null;
                }
                var decision = isExcluded || state is null || state.DiscoveredRevision == 0 ? 0L : isCleared ? head?.Revision : null;
                var submission = submissions.GetValueOrDefault(id);
                var hasPending = pending.Contains(id) || submission is not null || !hasLocal || copy is null ||
                    state is null || state.AppliedRevision != copy.Revision || state.DiscoveredRevision > state.AppliedRevision ||
                    received.GetValueOrDefault(id) > state.AppliedRevision;
                var error = errors.GetValueOrDefault(id);
                if (error is not null && ((!isParticipating && submission is null) || !hasPending))
                {
                    // A confirmed stop or installed result can resolve work outside this
                    // attempt. Runtime diagnostics must not outlive that durable outcome.
                    resolvedErrors.Add(id);
                    error = null;
                }

                var failure = submission?.QuotaRejection is { } rejection ?
                    MessagePackSerializer.Deserialize<ChatSyncFailure>(rejection, cancellationToken: token) :
                    (error as ChatSyncException)?.Failure;
                var progress = !isParticipating ? (ChatSyncProgress?)null :
                    failure is not null || error is not null ? ChatSyncProgress.Failed :
                    hasPending ? ChatSyncProgress.Pending : ChatSyncProgress.Synced;
                items.Add(
                    new ChatSyncItem(
                        id,
                        hasLocal ? chat?.Topic : copy?.Title,
                        hasLocal,
                        copy,
                        decision,
                        isParticipating ? ChatSyncParticipation.Cloud : ChatSyncParticipation.LocalOnly,
                        progress,
                        failure)
                    {
                        UpdatedAt = chat?.UpdatedAt,
                        CanReleaseSpace =
                            progress == ChatSyncProgress.Synced &&
                            copy is { Bytes: > 0 } &&
                            hasLocal &&
                            !_manager.HasUncommittedWork(id)
                    });
            }

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (chatId.HasValue)
                {
                    _items.Remove(chatId.Value);
                }
                else
                {
                    _items.Clear();
                }
                foreach (var item in items)
                {
                    _items[item.Id] = item;
                }
                foreach (var id in resolvedErrors)
                {
                    if (_errors.TryGetValue(id, out var current) && ReferenceEquals(current, errors[id]))
                    {
                        _errors.Remove(id);
                    }
                }
                BuildInventoryLocked();
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _projectionGate.Release();
        }
    }

    private void BuildInventoryLocked()
    {
        if (_usage is not { } usage)
        {
            return;
        }

        var items = _items.Values
            .AsValueEnumerable()
            .Select(item =>
            {
                var work = _work.GetValueOrDefault(item.Id);
                return item with
                {
                    Operation = work?.Operation ?? ChatSyncOperation.None,
                    Progress = item.Participation == ChatSyncParticipation.Cloud && work?.Transfer is not null ? ChatSyncProgress.Syncing : item.Progress,
                    CanReleaseSpace = item.CanReleaseSpace && work?.Transfer is null && (work is null || work.Operation == ChatSyncOperation.None)
                };
            })
            .OrderByDescending(item => item.UpdatedAt)
            .ThenByDescending(item => item.Id)
            .ToArray();
        _state = _state with
        {
            IsSynchronizing = _synchronization is not null || _work.Values.AsValueEnumerable().Any(work => work.Transfer is not null),
            Inventory = new ChatSyncInventory(AccountId, _transport.Compatibility.SyncGeneration, usage, items)
        };
    }

    private void PublishInventory()
    {
        lock (_gate)
        {
            if (_state.Inventory is not null)
            {
                BuildInventoryLocked();
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Reads one installed local decision for the history menu, without enumerating cloud copies.
    /// </summary>
    public async Task<ChatSyncDecision?> ReadDecisionAsync(Guid chatId, CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        await EnsureInitializedAsync(cancellation.Token).ConfigureAwait(false);
        await ReloadItemsAsync(chatId, cancellation.Token).ConfigureAwait(false);
        var inventory = State.Inventory;
        return inventory?.Items.AsValueEnumerable().FirstOrDefault(entry => entry.Id == chatId) is { } item ? inventory.DecisionFor(item) : null;
    }
}
using Avalonia.Threading;
using Everywhere.Cloud;
using Everywhere.Storage;

namespace Everywhere.Chat;

partial class ChatContextManager
{
    /// <summary>
    /// Freezes a submission from committed local history. Only the idle check and detached
    /// capture exclude new work. The commit gate keeps later saves out until freezing finishes.
    /// </summary>
    internal async Task PrepareUploadAsync(string accountId, Guid chatId, CancellationToken cancellationToken)
    {
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            var root = FindRoot(chatId);
            var admission = root?.TryDeferWorkWhenIdle();
            var isStable = root is null || admission is not null;
            var pending = GetPendingSave(chatId);
            var capture = default(CapturedChatWrite);
            try
            {
                if (pending is not null)
                {
                    capture = await CapturePendingSaveAsync(pending).ConfigureAwait(false);
                }
            }
            finally
            {
                admission?.SetResult();
            }

            if (pending is not null && capture is not null)
            {
                await CommitPendingSaveAsync(pending, capture).ConfigureAwait(false);
            }
            await _chatContextStorage.FreezeUploadAsync(accountId, chatId, isStable, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }

    /// <summary>
    /// Commits a received snapshot and publishes it to existing objects before admitting
    /// waiting work. Ordinary merges defer when busy; archive and deletion drain the root.
    /// </summary>
    internal async Task<bool> CommitReceivedSnapshotAsync(
        string accountId,
        ChatSyncDownloadedSnapshot download,
        bool shouldDiscardSubmission,
        CancellationToken cancellationToken)
    {
        var chatId = Guid.ParseExact(download.Head.ChatId, "D");
        await _commitGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        var root = default(RootChatContext);
        var admission = default(TaskCompletionSource);
        var hasStopped = false;
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            var hasContent = download.Head.Availability == ChatSyncAvailability.Available;
            root = FindRoot(chatId);
            if (root is not null)
            {
                if (download.Head.Lifecycle == ChatSyncLifecycle.Deleted ||
                    hasContent && download.Head.Lifecycle == ChatSyncLifecycle.Archived && !root.Data.Metadata.IsArchived)
                {
                    await root.StopAsync().ConfigureAwait(false);
                    hasStopped = true;
                }
                else if (hasContent)
                {
                    admission = root.TryDeferWorkWhenIdle();
                    if (admission is null)
                    {
                        return false;
                    }
                }
            }
            if (download.Head.Lifecycle != ChatSyncLifecycle.Deleted)
            {
                await SavePendingChatAsync(chatId).ConfigureAwait(false);
            }
            var currentRoot = root;
            var loaded = !hasContent || currentRoot is null || download.Head.Lifecycle == ChatSyncLifecycle.Deleted ?
                [] :
                await currentRoot
                    .DispatchAsync(() =>
                        currentRoot.Data.GetAllNodes().AsValueEnumerable().Where(node => node.IsMessageLoaded).Select(node => node.Id).ToHashSet())
                    .ConfigureAwait(false);
            var result = await _chatContextStorage.MergeSnapshotAsync(
                accountId,
                download,
                loaded,
                shouldDiscardSubmission,
                cancellationToken).ConfigureAwait(false);

            if (result is null)
            {
                return true;
            }

            using (_rootsLock.EnterScope())
            {
                foreach (var pending in _loads.Values.Where(pending => pending.ChatId == chatId))
                {
                    pending.ShouldReload = true;
                }
            }

            // Commit wins cancellation. Publish into existing identities before releasing work admission.
            var row = result.Metadata;
            if (row is null)
            {
                await RemoveDeletedChatAsync(chatId, root).ConfigureAwait(false);
                hasStopped = false;
                return true;
            }
            var metadata = _metadataMap.GetOrAdd(chatId, _ => ChatMetadataMapper.Materialize(row));
            if (root is not null)
            {
                await root.DispatchAsync(() =>
                {
                    foreach (var (id, message) in result.Messages)
                    {
                        var node = root.Data.FindNode(id) ?? throw new InvalidDataException("A loaded node disappeared during sync installation.");
                        root.DetachNestedContexts(node.Message);
                        node.InstallRemoteMessage(message);
                        root.AttachNestedContexts(message);
                    }

                    root.Data.ApplyCommittedTopology(result.Nodes, ReferenceEquals(_current, root));
                    ChatMetadataMapper.Publish(row, metadata);
                }).ConfigureAwait(false);
            }
            else
            {
                ChatMetadataMapper.Publish(row, metadata);
            }

            Dispatcher.UIThread.PostOnDemand(() => RestartHistoryMaterializationCore(resetRawHistory: true, preserveVisibleItems: true));
            if (row.ArchivedAt is not null)
            {
                QueueLeaveUnavailableChat(chatId);
            }
        }
        finally
        {
            if (hasStopped && !_isClosing)
            {
                root?.ResumeAfterStop();
            }
            admission?.SetResult();
            _commitGate.Release();
        }

        return true;
    }

    /// <summary>
    /// Commits a discovered stop independently of body transfers. Local work keeps running;
    /// flush its saved changes before changing participation, without replacing runtime objects.
    /// </summary>
    internal async Task CommitClearedSyncHeadAsync(string accountId, ChatSyncHead head, CancellationToken token)
    {
        await _commitGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_isClosing, this);
            await SavePendingChatAsync(Guid.ParseExact(head.ChatId, "D")).ConfigureAwait(false);
            await _chatContextStorage.ApplyClearedHeadAsync(accountId, head, token).ConfigureAwait(false);
        }
        finally
        {
            _commitGate.Release();
        }
    }
}
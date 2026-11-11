using Microsoft.Extensions.Logging;

namespace Everywhere.Cloud;

partial class ChatSyncSession
{
    /// <summary>
    /// Persists participation and publishes waiting state. Uploading is scheduled separately.
    /// </summary>
    public Task StartAsync(ChatSyncDecision decision, CancellationToken cancellationToken)
    {
        return ChangeParticipationAsync(decision, ChatSyncOperation.Starting, null, cancellationToken);
    }

    /// <summary>
    /// Removes exactly the confirmed copy and installs its result before returning.
    /// Only a conflicting transfer for this chat is canceled; local assistant work continues.
    /// </summary>
    public Task StopAsync(ChatSyncDecision decision, Guid confirmationId, CancellationToken cancellationToken)
    {
        return ChangeParticipationAsync(decision, ChatSyncOperation.Stopping, confirmationId, cancellationToken);
    }

    private async Task ChangeParticipationAsync(
        ChatSyncDecision decision,
        ChatSyncOperation operation,
        Guid? confirmationId,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = cancellation.Token;
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        if (decision.AccountId != AccountId || decision.Generation != _transport.Compatibility.SyncGeneration)
        {
            throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
        }

        var item = decision.Item;
        var work = GetWork(item.Id);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (work.Operation != ChatSyncOperation.None)
            {
                throw new InvalidOperationException("A participation command already owns this chat.");
            }

            work.Operation = operation;
            work.Management = completion.Task;
            var transfer = work.Transfer;
            // Cancel while the work ownership is stable. Disposal also uses this gate;
            // callbacks do not acquire account state or publish synchronously.
            transfer?.Cancel();
        }

        PublishInventory();
        var hasAdmission = false;
        var shouldSchedule = false;
        try
        {
            await work.Gate.WaitAsync(token).ConfigureAwait(false);
            hasAdmission = true;
            if (operation == ChatSyncOperation.Starting)
            {
                if (!item.CanStart || item.LocalDecisionRevision is not { } revision)
                {
                    throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
                }
                if (revision == 0)
                {
                    await _storage.SetUnpublishedParticipationAsync(AccountId, item.Id, true, token).ConfigureAwait(false);
                }
                else
                {
                    await _storage.RequestRepublishAsync(AccountId, item.Id, revision, token).ConfigureAwait(false);
                }
                shouldSchedule = true;
            }
            else if (item.Copy is { } copy)
            {
                var confirmation = new ChatSyncStopConfirmation(
                    (confirmationId ?? throw new InvalidOperationException("A stop must identify its confirmation.")).ToString("D"),
                    copy.Revision,
                    copy.LifecycleRevision);
                ChatSyncCommitResult receipt;
                try
                {
                    receipt = await _transport.StopAsync(item.Id, confirmation, token).ConfigureAwait(false);
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException && !token.IsCancellationRequested)
                {
                    receipt = await _transport.StopAsync(item.Id, confirmation, token).ConfigureAwait(false);
                }

                RecordReceipt(receipt);
                // The retained wire receipt has revision/usage, not a complete head. Read
                // only this chat to install the authoritative stop, never an account pass.
                // No body exists for a cleared head. A later explicit republish remains
                // a newer remote decision and must not be fabricated into a local stop.
                await ReceiveAsync(item.Id, true, token).ConfigureAwait(false);
            }
            else if (item.LocalDecisionRevision is > 0 and var revision)
            {
                await _storage.WithdrawRepublishAsync(AccountId, item.Id, revision, token).ConfigureAwait(false);
            }
            else
            {
                await _storage.SetUnpublishedParticipationAsync(AccountId, item.Id, false, token).ConfigureAwait(false);
            }

            lock (_gate)
            {
                _errors.Remove(item.Id);
            }

            await ReloadItemsAsync(item.Id, token).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                work.Operation = ChatSyncOperation.None;
            }

            if (hasAdmission)
            {
                work.Gate.Release();
            }

            PublishInventory();
            completion.TrySetResult();
            if (shouldSchedule && !_lifetime.IsCancellationRequested)
            {
                QueueChat(item.Id, false, _lifetime.Token, hasNewChanges: true).Detach(_logger.ToExceptionHandler());
                RequestSync();
            }
        }
    }

    /// <summary>
    /// Stops only the approved, still-safe copies. Each completed stop is immediately
    /// durable and visible; uploading the protected target is scheduled, not awaited.
    /// </summary>
    public async Task<ChatSyncReleaseResult> ReleaseSpaceAsync(
        ChatSyncInventory inventory,
        ChatSyncSpacePlan confirmed,
        CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        var token = cancellation.Token;
        await EnsureInitializedAsync(token).ConfigureAwait(false);
        if (inventory.AccountId != AccountId || inventory.Generation != _transport.Compatibility.SyncGeneration)
        {
            throw new ChatSyncException(ChatSyncFailureCode.AccountMismatch);
        }

        await _manager.FlushPendingSavesAsync().ConfigureAwait(false);
        await ReloadItemsAsync(null, token).ConfigureAwait(false);
        var current = State.Inventory ?? throw new InvalidOperationException("The session inventory is unavailable.");
        var plan = ChatSyncSpacePlan.FromInventory(current);
        if (plan.RequiredBytes == 0)
        {
            RequestSync();
            return new ChatSyncReleaseResult(0, false);
        }

        if (plan.Target != confirmed.Target || plan.Copies.Count == 0 ||
            plan.Copies.Any(candidate =>
                !confirmed.Copies.Any(original =>
                    original.Id == candidate.Id && original.Copy == candidate.Copy && original.UpdatedAt == candidate.UpdatedAt)))
        {
            throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
        }

        var growth = plan.RequiredBytes + current.Usage.LimitBytes - current.Usage.UsedBytes;
        var stopped = 0;
        var needsReview = false;
        try
        {
            foreach (var approved in plan.Copies)
            {
                await _manager.FlushPendingSavesAsync().ConfigureAwait(false);
                var decision = await ReadDecisionAsync(approved.Id, token).ConfigureAwait(false);
                if (decision?.Item is not { CanReleaseSpace: true } item || item.Copy != approved.Copy || item.UpdatedAt != approved.UpdatedAt)
                {
                    throw new ChatSyncException(ChatSyncFailureCode.RevisionConflict);
                }

                await StopAsync(decision, Guid.NewGuid(), token).ConfigureAwait(false);
                stopped++;
                if (State.Inventory is { Usage: var usage } && usage.UsedBytes + growth <= usage.LimitBytes)
                {
                    break;
                }
            }
        }
        catch (Exception error) when (stopped > 0 && error is not OperationCanceledException)
        {
            needsReview = true;
            _logger.LogWarning(error, "Space cleanup stopped after {Count} confirmed removals.", stopped);
        }

        lock (_gate)
        {
            _priorityChat = plan.Target;
        }

        RequestPass(true, true, CancellationToken.None).Detach(_logger.ToExceptionHandler());
        return new ChatSyncReleaseResult(stopped, needsReview);
    }
}
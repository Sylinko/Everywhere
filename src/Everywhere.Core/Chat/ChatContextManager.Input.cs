using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Storage;

namespace Everywhere.Chat;

partial class ChatContextManager
{
    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings + 2;

    /// <summary>
    /// Restores the one unsent chat after storage initialization,
    /// before windows can publish an input binding. It does not create a history row.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var saved = await _chatContextStorage.ReadNewChatInputAsync(cancellationToken);
        if (saved is null)
        {
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                var now = DateTimeOffset.UtcNow;
                var metadata = new ChatMetadata(saved.ChatId, now, now, null);
                var root = CreateRoot(new ChatData(metadata));
                RestoreLocalInput(root, saved.Snapshot);
                _newChatDraft = root;
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private void RestoreLocalInput(RootChatContext root, ChatInputSnapshot snapshot)
    {
        root.Input.Restore(snapshot);
        root.PendingInputs.Restore(
            snapshot.PendingInputs,
            ResolveCustomAssistant,
            pauseDrain: !root.IsTurnActive);
    }

    private CustomAssistant? ResolveCustomAssistant(Guid id) =>
        _settings.Model.CustomAssistants.AsValueEnumerable().FirstOrDefault(candidate => candidate.Id == id);

    private void HandleInputChanged(object? sender, EventArgs e)
    {
        if (sender is ChatInputState input)
        {
            Dispatcher.UIThread.PostOnDemand(() => EnqueueInputSave(input.ChatId));
        }
    }

    private void HandlePendingInputsChanged(object? sender, EventArgs e)
    {
        if (sender is ChatPendingInputQueue queue)
        {
            Dispatcher.UIThread.PostOnDemand(() => EnqueueInputSave(queue.ChatId));
        }
    }

    private void EnqueueInputSave(Guid chatId)
    {
        if (_isClosing || FindRoot(chatId) is not { Data.IsDisposed: false } root || !_metadataMap.ContainsKey(chatId))
        {
            return;
        }

        var write = root.Data.Metadata.IsTemporary ?
            new ChatInputWrite(null, []) :
            ChatInputWrite.Capture(root.Input, root.PendingInputs);
        write = write with { IsNewChatDraft = ReferenceEquals(root, _newChatDraft) };
        lock (_pendingInputs)
        {
            _pendingInputs[chatId] = new PendingInput(root, write);
        }

        _saveDebounceExecutor.Trigger();
    }

    private PendingInput? GetPendingInput(Guid chatId)
    {
        lock (_pendingInputs)
        {
            return _pendingInputs.GetValueOrDefault(chatId);
        }
    }

    private void AcknowledgeInput(Guid id, ChatInputWrite input)
    {
        lock (_pendingInputs)
        {
            if (_pendingInputs.TryGetValue(id, out var current) && ReferenceEquals(current.Write, input))
            {
                _pendingInputs.Remove(id);
            }
        }
    }

    private async Task CommitPendingInputsAsync(List<Exception> failures)
    {
        PendingInput[] inputs;
        lock (_pendingInputs)
        {
            inputs = [.. _pendingInputs.Values];
        }

        foreach (var input in inputs)
        {
            // Pending graph writes, including failed or newer revisions, commit their input
            // in the graph transaction. An unrelated failure must not block input-only drafts.
            if (GetPendingSave(input.Root.Data.Metadata.Id) is not null)
            {
                continue;
            }

            try
            {
                // Accepted local input survives navigation and shutdown. No UI access is
                // needed here, and cancellation must not discard its last captured values.
                if (await _chatContextStorage.SaveInputAsync(input.Root.Data.Metadata.Id, input.Write, CancellationToken.None).ConfigureAwait(false))
                {
                    AcknowledgeInput(input.Root.Data.Metadata.Id, input.Write);
                }
            }
            catch (Exception exception)
            {
                failures.Add(new IOException($"Failed to save input for conversation {input.Root.Data.Metadata.Id}.", exception));
            }
        }
    }
}
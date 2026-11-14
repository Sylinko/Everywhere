using Avalonia.Threading;
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
                var data = new ChatData(metadata, [], new ChatMessageNode(Guid.CreateVersion7().SetVersion(0), new RootChatMessage()));
                var root = CreateRoot(data);
                root.Input.Restore(saved.Snapshot);
                _newChatDraft = root;
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private void HandleInputChanged(object? sender, EventArgs e)
    {
        if (_isClosing || sender is not ChatInputState input || FindRoot(input.ChatId) is not { } root || !_metadataMap.ContainsKey(input.ChatId))
        {
            return;
        }

        var write = root.Data.Metadata.IsTemporary ? new ChatInputWrite(null, []) : ChatInputWrite.Capture(input);
        write = write with { IsNewChatDraft = ReferenceEquals(root, _newChatDraft) };
        lock (_pendingInputs)
        {
            _pendingInputs[input.ChatId] = new PendingInput(root, write);
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
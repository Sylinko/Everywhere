namespace Everywhere.Chat;

partial class ChatContext
{
    private readonly Dictionary<Guid, Task> _turnLoads = new();

    /// <summary>
    /// Shares whole-turn loading across readers. Canceling a viewport only stops that reader's
    /// wait; root shutdown cancels the actual I/O and awaits installation or discarded results.
    /// </summary>
    public async Task EnsureTurnLoadedAsync(ChatTurn? turn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var loading = await DispatchAsync(() =>
        {
            var nodes = turn?.Nodes ?? Data.GetAllNodes().Where(node => node.Turn is null);
            if (nodes.AsValueEnumerable().All(node => node.IsMessageLoaded))
            {
                return Task.CompletedTask;
            }

            var key = turn?.Id ?? Guid.Empty;
            if (!_turnLoads.TryGetValue(key, out var task) || task.IsFaulted || task.IsCanceled)
            {
                task = RunBackgroundAsync(token => LoadTurnAsync(turn?.Id, token), CancellationToken.None);
                _turnLoads[key] = task;
            }

            return task;
        }).ConfigureAwait(false);
        await loading.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadTurnAsync(Guid? turnId, CancellationToken cancellationToken)
    {
        var loader = Root.Loader ?? throw new InvalidOperationException("The conversation has no stored message source.");
        var messages = await loader.LoadMessagesAsync(Data.Metadata.Id, turnId, cancellationToken).ConfigureAwait(false);
        try
        {
            await DispatchAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (id, message) in messages)
                {
                    var node = Data.FindNode(id) ?? throw new InvalidDataException("A loaded body has no topology node.");
                    if (node.TryLoadMessage(message))
                    {
                        AttachNestedContexts(message);
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            // Ownership moves to the existing node on installation. Canceled/unused bodies were
            // never published and can release their resources without involving a presentation.
            foreach (var message in messages.Values)
            {
                if (message.Owner is null && message is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
    }

    /// <summary>
    /// Reads covered user text without materializing the remainder of its turn.
    /// </summary>
    public async Task<string> ReadUserTextAsync(ChatMessageNode node, CancellationToken cancellationToken = default)
    {
        if (node.IsMessageLoaded)
        {
            return ((UserChatMessage)node.Message).Content;
        }

        var loader = Root.Loader ?? throw new InvalidOperationException("The conversation has no stored message source.");
        var text = string.Empty;
        await RunBackgroundAsync(
            async token => text = await loader.ReadUserTextAsync(Data.Metadata.Id, node.Id, node.MessageKind, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return node.IsMessageLoaded ? ((UserChatMessage)node.Message).Content : text;
    }

    internal void AttachNestedContexts(ChatMessage message)
    {
        foreach (var child in ChatMessageResources.GetChildData(message))
        {
            if (!Root.HasNested(child))
            {
                CreateNested(child);
            }
        }
    }
}
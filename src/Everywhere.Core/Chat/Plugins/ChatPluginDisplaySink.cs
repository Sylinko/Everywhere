using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Stores durable display blocks. This is a model collection, not a plugin execution service;
/// attached collections are mutated only within their root conversation's execution domain.
/// </summary>
[MessagePackObject(SuppressSourceGeneration = true)]
[MessagePackFormatter(typeof(ChatPluginDisplaySinkFormatter))]
public sealed class ChatPluginDisplaySink : IReadOnlyList<ChatPluginDisplayBlock>, IDisposable
{
    public int Count => Items.Count;

    public IReadOnlyList<ChatPluginDisplayBlock> Items => Volatile.Read(ref _items);

    public ChatPluginDisplayBlock this[int index] => Items[index];

    /// <summary>
    /// Signals structural changes for standalone views or the owning function. Descendant content
    /// changes notify their node directly rather than bubbling through every container.
    /// </summary>
    public event EventHandler? Changed;

    private IReadOnlyList<ChatPluginDisplayBlock> _items = Array.Empty<ChatPluginDisplayBlock>();
    private ChatMessageNode? _owner;
    private bool _isDisposed;

    internal void SetOwner(ChatMessageNode? owner)
    {
        _owner = owner;
        foreach (var block in _items)
        {
            block.Owner = owner;
        }
    }

    public void AppendBlock(ChatPluginDisplayBlock block) => AppendBlocks(new[] { block });

    public void Add(ChatPluginDisplayBlock block) => AppendBlock(block);

    public void AppendBlocks(IEnumerable<ChatPluginDisplayBlock> blocks)
    {
        var added = blocks.ToArray();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        foreach (var block in added)
        {
            block.Owner = _owner;
        }

        Volatile.Write(ref _items, _items.Concat(added).ToArray());

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void AppendText(string text, string? className = null)
    {
        AppendBlock(new ChatPluginTextDisplayBlock(text, className));
    }

    public IProgress<double> AppendProgress(IDynamicLocaleKey headerKey)
    {
        var progressBlock = new ChatPluginProgressDisplayBlock(headerKey);
        AppendBlock(progressBlock);
        return progressBlock.ProgressReporter;
    }


    public void AppendSubagent(ChatData chatData)
    {
        AppendBlock(new ChatPluginSubagentDisplayBlock(chatData));
    }

    public IEnumerator<ChatPluginDisplayBlock> GetEnumerator() => Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _owner = null;
        foreach (var block in _items)
        {
            block.Owner = null;
            if (block is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

public sealed class ChatPluginDisplaySinkFormatter : IMessagePackFormatter<ChatPluginDisplaySink?>
{
    public void Serialize(ref MessagePackWriter writer, ChatPluginDisplaySink? value, MessagePackSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNil();
        }
        else
        {
            var formatter = options.Resolver.GetFormatterWithVerify<ChatPluginDisplayBlock>();
            var items = value.Items;
            var count = items.Count;
            writer.WriteArrayHeader(count);
            for (var i = 0; i < count; i++)
            {
                writer.CancellationToken.ThrowIfCancellationRequested();
                formatter.Serialize(ref writer, items[i], options);
            }
        }
    }

    public ChatPluginDisplaySink? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        var formatter = options.Resolver.GetFormatterWithVerify<ChatPluginDisplayBlock?>();
        var count = reader.ReadArrayHeader();
        var items = new List<ChatPluginDisplayBlock>(count);
        var result = new ChatPluginDisplaySink();
        options.Security.DepthStep(ref reader);
        try
        {
            for (var i = 0; i < count; i++)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
                if (formatter.Deserialize(ref reader, options) is not { } item) continue;
                items.Add(item);
            }

            result.AppendBlocks(items);
        }
        finally
        {
            reader.Depth--;
        }

        return result;
    }
}
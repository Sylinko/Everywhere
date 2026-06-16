using DynamicData;
using Everywhere.Common;
using MessagePack;
using MessagePack.Formatters;
using ZLinq;

namespace Everywhere.Chat.Plugins;

[MessagePackObject(SuppressSourceGeneration = true)]
[MessagePackFormatter(typeof(ChatPluginDisplaySinkFormatter))]
public sealed class ChatPluginDisplaySink : IReadOnlyList<ChatPluginDisplayBlock>, ISourceList<ChatPluginDisplayBlock>, IChatPluginDisplaySink
{
    public int Count => _itemsSource.Count;

    public IObservable<int> CountChanged => _itemsSource.CountChanged;

    public IReadOnlyList<ChatPluginDisplayBlock> Items => _itemsSource.Items;

    public ChatPluginDisplayBlock this[int index] => _itemsSource.Items[index];

    private readonly SourceList<ChatPluginDisplayBlock> _itemsSource = new();

    public void AppendBlock(ChatPluginDisplayBlock block)
    {
        _itemsSource.Add(block);
    }

    public void Add(ChatPluginDisplayBlock block)
    {
        _itemsSource.Add(block);
    }

    public void AppendBlocks(IEnumerable<ChatPluginDisplayBlock> blocks)
    {
        _itemsSource.AddRange(blocks);
    }

    public IChatPluginDisplaySink AppendContainer()
    {
        var groupBlock = new ChatPluginContainerDisplayBlock();
        _itemsSource.Add(groupBlock);
        return groupBlock.DisplaySink;
    }

    public void AppendText(string text, string? className = null)
    {
        _itemsSource.Add(new ChatPluginTextDisplayBlock(text, className));
    }

    public void AppendDynamicLocaleKey(IDynamicLocaleKey resourceKey, string? className = null)
    {
        _itemsSource.Add(new ChatPluginDynamicLocaleKeyDisplayBlock(resourceKey, className));
    }

    public ThreadSafeObservableStringBuilder AppendMarkdown()
    {
        var markdownBlock = new ChatPluginMarkdownDisplayBlock();
        _itemsSource.Add(markdownBlock);
        return markdownBlock.MarkdownBuilder;
    }

    public IProgress<double> AppendProgress(IDynamicLocaleKey headerKey)
    {
        var progressBlock = new ChatPluginProgressDisplayBlock(headerKey);
        _itemsSource.Add(progressBlock);
        return progressBlock.ProgressReporter;
    }

    public void AppendFileReferences(params IReadOnlyList<ChatPluginFileReference> references)
    {
        _itemsSource.Add(new ChatPluginFileReferencesDisplayBlock(references));
    }

    public void AppendFileDifference(TextDifference difference, string originalText)
    {
        _itemsSource.Add(new ChatPluginFileDifferenceDisplayBlock(difference, originalText));
    }

    public void AppendUrls(IReadOnlyList<ChatPluginUrl> urls)
    {
        _itemsSource.Add(new ChatPluginUrlsDisplayBlock(urls));
    }

    public void AppendSeparator(double thickness = 1)
    {
        _itemsSource.Add(new ChatPluginSeparatorDisplayBlock(thickness));
    }

    public void AppendCodeBlock(string code, string? language = null)
    {
        _itemsSource.Add(new ChatPluginCodeBlockDisplayBlock(code, language));
    }

    public void AppendChatContext(ChatContext chatContext)
    {
        _itemsSource.Add(new ChatPluginChatContextDisplayBlock(chatContext));
    }

    public IEnumerator<ChatPluginDisplayBlock> GetEnumerator() => _itemsSource.Items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public IObservable<IChangeSet<ChatPluginDisplayBlock>> Connect(Func<ChatPluginDisplayBlock, bool>? predicate = null)
    {
        return _itemsSource.Connect(predicate);
    }

    public IObservable<IChangeSet<ChatPluginDisplayBlock>> Preview(Func<ChatPluginDisplayBlock, bool>? predicate = null)
    {
        return _itemsSource.Preview(predicate);
    }

    public void Edit(Action<IExtendedList<ChatPluginDisplayBlock>> updateAction)
    {
        _itemsSource.Edit(updateAction);
    }

    public void Dispose()
    {
        _itemsSource.Edit(list =>
        {
            foreach (var disposable in list.AsValueEnumerable().OfType<IDisposable>())
            {
                disposable.Dispose();
            }
        });

        _itemsSource.Dispose();
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
            var count = value.Count;
            writer.WriteArrayHeader(count);
            for (var i = 0; i < count; i++)
            {
                writer.CancellationToken.ThrowIfCancellationRequested();
                formatter.Serialize(ref writer, value[i], options);
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
        var result = new ChatPluginDisplaySink();
        options.Security.DepthStep(ref reader);
        try
        {
            for (var i = 0; i < count; i++)
            {
                reader.CancellationToken.ThrowIfCancellationRequested();
                if (formatter.Deserialize(ref reader, options) is not { } item) continue;
                result.AppendBlock(item);
            }
        }
        finally
        {
            reader.Depth--;
        }

        return result;
    }
}

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Publishes plugin output in the owning conversation's serial execution domain.
/// Completion means the block has been attached to the message, not that a view has rendered it.
/// </summary>
public interface IChatPluginDisplaySink
{
    /// <summary>
    /// Transfers a prepared block to the conversation. Subsequent changes to an attached block
    /// must also be dispatched through its conversation runtime.
    /// </summary>
    Task AppendBlockAsync(ChatPluginDisplayBlock block);

    /// <summary>
    /// Publishes a prepared collection of blocks together.
    /// </summary>
    Task AppendBlocksAsync(IEnumerable<ChatPluginDisplayBlock> blocks);

    /// <summary>
    /// Publishes localized output without resolving the user's current language.
    /// </summary>
    Task AppendDynamicLocaleKeyAsync(IDynamicLocaleKey resourceKey, string? className = null)
    {
        return AppendBlockAsync(new ChatPluginDynamicLocaleKeyDisplayBlock(resourceKey, className));
    }

    /// <summary>
    /// Publishes references to files involved in this invocation.
    /// </summary>
    Task AppendFileReferencesAsync(params IReadOnlyList<ChatPluginFileReference> references)
    {
        return AppendBlockAsync(new ChatPluginFileReferencesDisplayBlock(references));
    }

    /// <summary>
    /// Publishes the URLs involved in this invocation.
    /// </summary>
    Task AppendUrlsAsync(IReadOnlyList<ChatPluginUrl> urls) => AppendBlockAsync(new ChatPluginUrlsDisplayBlock(urls));

    /// <summary>
    /// Publishes a nested conversation without transferring its runtime lifetime to the view.
    /// </summary>
    Task AppendSubagentAsync(ChatData chatData) => AppendBlockAsync(new ChatPluginSubagentDisplayBlock(chatData));
}
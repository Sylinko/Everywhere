using Everywhere.Common;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Allows chat plugins to display content in the user interface.
/// </summary>
public interface IChatPluginDisplaySink
{
    /// <summary>
    /// Appends a display block to the display sink.
    /// </summary>
    /// <param name="block"></param>
    void AppendBlock(ChatPluginDisplayBlock block);

    /// <summary>
    /// Appends multiple display blocks to the display sink.
    /// </summary>
    /// <param name="blocks"></param>
    void AppendBlocks(IEnumerable<ChatPluginDisplayBlock> blocks);

    /// <summary>
    /// Appends a group block to the display sink. The caller can use the returned sink to append content to the group.
    /// </summary>
    /// <returns></returns>
    IChatPluginDisplaySink AppendContainer();

    /// <summary>
    /// Appends plain text to the display sink.
    /// </summary>
    /// <param name="text"></param>
    /// <param name="className"></param>
    void AppendText(string text, string? className = null);

    /// <summary>
    /// Appends a dynamic resource key to the display sink.
    /// </summary>
    /// <param name="resourceKey"></param>
    /// <param name="className"></param>
    void AppendDynamicLocaleKey(IDynamicLocaleKey resourceKey, string? className = null);

    /// <summary>
    /// Appends a Markdown builder to the display sink. The caller can use the returned builder to build markdown content.
    /// </summary>
    /// <returns></returns>
    ThreadSafeObservableStringBuilder AppendMarkdown();

    /// <summary>
    /// Appends a progress indicator to the display sink. The caller can use the returned progress reporter to report progress between 0.0 and 1.0.
    /// </summary>
    /// <returns>a progress reporter that accepts values between 0.0 and 1.0, NaN for indeterminate progress</returns>
    IProgress<double> AppendProgress(IDynamicLocaleKey headerKey);

    /// <summary>
    /// Appends a file reference to the display sink.
    /// </summary>
    /// <param name="references"></param>
    void AppendFileReferences(params IReadOnlyList<ChatPluginFileReference> references);

    /// <summary>
    /// Appends a text file difference to the display sink and waits for the user to review it.
    /// </summary>
    /// <param name="difference"></param>
    /// <param name="originalText"></param>
    void AppendFileDifference(TextDifference difference, string originalText);

    /// <summary>
    /// Appends a list of URLs to the display sink.
    /// </summary>
    /// <param name="urls"></param>
    void AppendUrls(IReadOnlyList<ChatPluginUrl> urls);

    /// <summary>
    /// Appends a separator to the display sink.
    /// </summary>
    void AppendSeparator(double thickness = 1.0d);

    /// <summary>
    /// Appends a code block to the display sink.
    /// </summary>
    /// <param name="code"></param>
    /// <param name="language"></param>
    void AppendCodeBlock(string code, string? language = null);

    /// <summary>
    /// Appends chat context information to the display sink. Used for subagents that need to display a subconversation.
    /// </summary>
    /// <param name="chatContext"></param>
    void AppendChatContext(ChatContext chatContext);
}
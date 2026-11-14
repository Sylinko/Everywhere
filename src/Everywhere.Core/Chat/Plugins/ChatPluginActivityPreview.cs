namespace Everywhere.Chat.Plugins;

/// <summary>
/// Describes inexpensive, invocation-scoped content shown below a running activity header.
/// </summary>
/// <remarks>
/// Activity previews are deliberately separate from <see cref="ChatPluginDisplayBlock"/>. Display
/// blocks are durable detail content, while previews exist only for the lifetime of a tool
/// invocation and are never serialized. Keeping the two channels distinct prevents heavyweight
/// terminals, Markdown presenters, diffs, and editors from entering the collapsed activity tree.
/// </remarks>
public abstract class ChatPluginActivityPreview;

/// <summary>
/// Displays a localized single-line running status.
/// </summary>
public sealed class ChatPluginTextActivityPreview(IDynamicLocaleKey textKey) : ChatPluginActivityPreview
{
    public IDynamicLocaleKey TextKey { get; } = textKey;
}

/// <summary>
/// Displays a terminal command without constructing the terminal presenter.
/// </summary>
public sealed class ChatPluginCommandActivityPreview(string command) : ChatPluginActivityPreview
{
    public string Command { get; } = command;
}

/// <summary>
/// Displays an optional request parameter followed by a compact set of file references.
/// </summary>
public sealed class ChatPluginFileReferencesActivityPreview(
    IReadOnlyList<ChatPluginFileReference> references,
    IDynamicLocaleKey? prefixKey = null
) : ChatPluginActivityPreview
{
    public IDynamicLocaleKey? PrefixKey { get; } = prefixKey;

    public IReadOnlyList<ChatPluginFileReference> References { get; } = references;
}

/// <summary>
/// Displays a directional file request, such as a move or rename operation.
/// </summary>
public sealed class ChatPluginFileTransferActivityPreview(
    ChatPluginFileReference source,
    IDynamicLocaleKey actionKey,
    ChatPluginFileReference destination
) : ChatPluginActivityPreview
{
    public ChatPluginFileReference Source { get; } = source;

    public IDynamicLocaleKey ActionKey { get; } = actionKey;

    public ChatPluginFileReference Destination { get; } = destination;
}

/// <summary>
/// Displays compact URL capsules. The View derives host names and
/// favicon locations from the already validated URIs so plugins never need to fetch presentation
/// metadata or duplicate the image loader's cache.
/// </summary>
public sealed class ChatPluginUrlsActivityPreview(IReadOnlyList<Uri> urls) : ChatPluginActivityPreview
{
    public IReadOnlyList<Uri> Urls { get; } = urls.ToArray();
}

/// <summary>
/// Displays a subagent conversation header with its own independent dialog. The subagent's
/// <see cref="ChatData"/> is projected into the dialog, so the subagent's conversation is fully interactive without coupling the plugin to dialog services.
/// </summary>
/// <param name="chatData"></param>
public sealed class ChatPluginSubagentActivityPreview(ChatData chatData) : ChatPluginActivityPreview
{
    public ChatData ChatData { get; } = chatData;
}
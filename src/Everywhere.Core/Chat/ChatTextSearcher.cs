using LiveMarkdown.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// Defines the user-visible text that participates in chat search. Tool calls, function arguments,
/// results and plugin display blocks deliberately stay outside this projection.
/// </summary>
internal static class ChatTextSearcher
{
    /// <summary>
    /// An instance of MarkdownPipeline is immutable, thread-safe, and should be reused when parsing multiple inputs.
    /// </summary>
    public static MarkdownTextProjector SharedMarkdownTextProjector { get; } = new();

    public static async Task<bool> ContainsAsync(
        ChatContext context,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken)
    {
        // Search only the captured selected branch. A match can stop loading early; neither
        // off-branch turns nor their payloads are required for history-list filtering.
        var nodes = context.Data.Items;
        foreach (var node in nodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!node.IsMessageLoaded)
            {
                if (node.Turn is { } turn)
                {
                    await context.EnsureTurnLoadedAsync(turn, cancellationToken);
                }
                else
                {
                    await context.EnsureTurnLoadedAsync(null, cancellationToken);
                }
            }

            if (Contains(node.Message, pattern, markdownProjector, cancellationToken)) return true;
        }

        return false;
    }

    private static bool Contains(
        ChatMessage message,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken) => message switch
    {
        UserChatMessage { Content: { Length: > 0 } content } => pattern.FindRanges(content).Any(),
        AssistantChatMessage assistant => Contains(assistant, pattern, markdownProjector, cancellationToken),
        _ => false
    };

    private static bool Contains(
        AssistantChatMessage message,
        TextSearchPattern pattern,
        MarkdownTextProjector markdownProjector,
        CancellationToken cancellationToken)
    {
        var sources = new List<string>();
        foreach (var span in message.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (span is not AssistantChatMessageTextSpan textSpan || textSpan.ContentMarkdownBuilder.Length == 0)
            {
                continue;
            }

            sources.Add(textSpan.ContentMarkdownBuilder.ToString());
        }

        if (sources.Count == 0) return false;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Historical matching consumes this immutable snapshot immediately and does not
            // reconcile it with a live renderer, so no persistent source version is required.
            var projection = markdownProjector.Project(new ObservableStringBuilderSnapshot(source, 0), cancellationToken);
            if (projection.Buffers.AsValueEnumerable().Any(buffer => pattern.FindRanges(buffer.Text).AsValueEnumerable().Any())) return true;
        }

        return false;
    }
}
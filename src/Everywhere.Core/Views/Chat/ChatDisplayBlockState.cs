using Everywhere.Chat.Plugins;
using Everywhere.Terminal;

namespace Everywhere.Views;

/// <summary>
/// Captures only mutable display-block values. Immutable blocks remain their own render input.
/// </summary>
public static class ChatDisplayBlockState
{
    /// <summary>
    /// Creates a value snapshot without copying static file, URL or Markdown content.
    /// </summary>
    public static object? Capture(object? content) => content switch
    {
        ChatPluginContainerDisplayBlock container => new ChatContainerBlockState(container.Children),
        ChatPluginProgressDisplayBlock progress => new ChatProgressBlockState(progress.HeaderKey, progress.Progress),
        ChatPluginFileReferencesDisplayBlock files => new ChatFileReferencesBlockState(files.References, files.TotalReferenceCount),
        ChatPluginFileDifferenceDisplayBlock difference => new ChatDifferenceBlockState(
            difference.Difference,
            difference.OriginalText,
            difference.ReviewKind,
            difference.SourcePath,
            difference.FilePath,
            difference.AddedLineCount,
            difference.RemovedLineCount),
        ChatPluginSubagentDisplayBlock subagent => new ChatSubagentBlockState(subagent, subagent.ChatData.Metadata.Topic),
        _ => content
    };
}

/// <summary>
/// Container membership remains a stable reference snapshot until a child is added.
/// </summary>
public sealed record ChatContainerBlockState(IReadOnlyList<ChatPluginDisplayBlock> Children);

/// <summary>
/// Current progress; NaN retains its indeterminate meaning.
/// </summary>
public sealed record ChatProgressBlockState(IDynamicLocaleKey HeaderKey, double Progress);

/// <summary>
/// File-reference membership and the reported total count.
/// </summary>
public sealed record ChatFileReferencesBlockState(IReadOnlyList<ChatPluginFileReference> References, int TotalReferenceCount)
{
    public bool HasMoreReferences => TotalReferenceCount > References.Count;
}

/// <summary>
/// Captured review availability. The interactive difference object retains its own review state.
/// </summary>
public sealed record ChatDifferenceBlockState(
    TextDifference? Difference,
    string? OriginalText,
    TextDifferenceReviewKind ReviewKind,
    string? SourcePath,
    string FilePath,
    int AddedLineCount,
    int RemovedLineCount
)
{
    public bool CanReview => Difference is { TotalChangesCount: > 0 } && OriginalText is not null;

    public bool HasLineChanges => AddedLineCount > 0 || RemovedLineCount > 0;
}

/// <summary>
/// Captured child-chat title, with the block retained only as the navigation target.
/// </summary>
public sealed record ChatSubagentBlockState(ChatPluginSubagentDisplayBlock Block, string? Topic);

/// <summary>
/// Command identity and terminal completion values; output and input stay on the live run.
/// </summary>
public sealed record ChatTerminalState(ShellType ShellType, string? Command, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, int? ExitCode)
{
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - CreatedAt;

    public static ChatTerminalState Capture(ChatPluginTerminalDisplayBlock block)
    {
        return new ChatTerminalState(block.ShellType, block.Command, block.CreatedAt, block.FinishedAt, block.ExitCode);
    }
}
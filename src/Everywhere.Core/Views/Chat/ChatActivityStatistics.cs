using Everywhere.Chat.Plugins;
using ZLinq;

namespace Everywhere.Views;

/// <summary>
/// Contains the reliable counters derived from a set of projected chat activities.
/// </summary>
/// <remarks>
/// This is an immutable value snapshot rather than another observable presentation object. Activity
/// rows already own change notification and replace their <c>Statistics</c> property when the source
/// changes; value equality therefore suppresses redundant notifications without adding a nested
/// subscription or lifetime. New counters, such as files or fetched pages, belong here together with
/// their source traversal so Group and whole-turn summaries cannot drift apart.
/// </remarks>
public readonly record struct ChatActivityStatistics
{
    /// <summary>Gets the number of reasoning activities.</summary>
    public int ReasoningCount { get; init; }

    /// <summary>
    /// Gets the number of function-call activities presented to the user.
    /// </summary>
    /// <remarks>
    /// A <see cref="FunctionCallActivityItemPresentationRow"/> may contain several low-level calls
    /// after the execution pipeline groups equivalent tool invocations. Counting presentation rows
    /// keeps the summary consistent with the number of tool entries visible in the Timeline; the
    /// underlying call count remains available on the individual row when detailed execution data
    /// is needed.
    /// </remarks>
    public int ToolCallCount { get; init; }

    /// <summary>Gets the number of structured subagent contexts in function-call display blocks.</summary>
    public int SubagentCount { get; init; }

    /// <summary>
    /// Calculates one statistics snapshot from the supplied activity rows in a single traversal.
    /// </summary>
    /// <param name="items">The projected activities to aggregate.</param>
    /// <returns>A value containing all counters currently understood by the presentation layer.</returns>
    public static ChatActivityStatistics Calculate(IEnumerable<ActivityItemPresentationRow> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var reasoningCount = 0;
        var toolCallCount = 0;
        var subagentCount = 0;

        foreach (var item in items)
        {
            switch (item)
            {
                case ReasoningActivityItemPresentationRow:
                    reasoningCount++;
                    break;
                case FunctionCallActivityItemPresentationRow functionCall:
                    toolCallCount++;
                    subagentCount += functionCall.DisplayBlocks.AsValueEnumerable().Sum(CountSubagents);
                    break;
            }
        }

        return new ChatActivityStatistics
        {
            ReasoningCount = reasoningCount,
            ToolCallCount = toolCallCount,
            SubagentCount = subagentCount,
        };
    }

    private static int CountSubagents(ChatPluginDisplayBlock block) => block switch
    {
        ChatPluginSubagentDisplayBlock => 1,
        ChatPluginContainerDisplayBlock container => container.Children.Sum(CountSubagents),
        _ => 0,
    };
}

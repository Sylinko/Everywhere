using Everywhere.Chat.Plugins;

namespace Everywhere.Chat;

/// <summary>
/// Owns one nested conversation's runtime while borrowing its root's shared resources.
/// Ending a nested operation does not remove the conversation retained in its parent's data.
/// </summary>
public sealed class NestedChatContext(
    ChatContext parent,
    ChatData data,
    ToolPatternRulesets? toolPatternRulesets,
    GenerationApprovalState? inheritedApprovalState
) : ChatContext(data, toolPatternRulesets, inheritedApprovalState)
{
    /// <summary>
    /// Gets the context that created this nested conversation.
    /// </summary>
    public ChatContext Parent { get; } = parent;

    /// <inheritdoc />
    public override RootChatContext Root => Parent.Root;

    /// <inheritdoc />
    public override string WorkingDirectory => Root.WorkingDirectory;
}
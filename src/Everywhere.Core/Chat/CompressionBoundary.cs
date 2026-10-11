namespace Everywhere.Chat;

/// <summary>
/// Identifies the end of the conversation prefix selected by the caller for compression.
/// The node must belong to the current branch. Tool calls and their results must be complete
/// before the caller includes their assistant node.
/// </summary>
public readonly struct CompressionBoundary
{
    /// <summary>
    /// Gets the node at which the compression prefix ends.
    /// </summary>
    public ChatMessageNode Node { get; }

    /// <summary>
    /// Gets whether the selected prefix includes <see cref="Node"/>.
    /// </summary>
    public bool ShouldIncludeNode { get; }

    private CompressionBoundary(ChatMessageNode node, bool shouldIncludeNode)
    {
        Node = node;
        ShouldIncludeNode = shouldIncludeNode;
    }

    /// <summary>
    /// Selects the prefix before the node, preserving the node and subsequent messages verbatim.
    /// </summary>
    public static CompressionBoundary Before(ChatMessageNode node) => new(node, false);

    /// <summary>
    /// Selects the prefix through the node, including its content in the summary input.
    /// </summary>
    public static CompressionBoundary Through(ChatMessageNode node) => new(node, true);
}
namespace Everywhere.Chat.Documents;

/// <summary>
/// Provides concise, strongly typed modifiers for declarative prompt construction.
/// </summary>
public static class PromptNodeExtensions
{
    /// <summary>
    /// Sets a node's priority and returns the same concrete node.
    /// </summary>
    public static T WithPriority<T>(this T node, int priority) where T : PromptNode
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Priority = priority;
        return node;
    }

    extension<T>(T container) where T : PromptContainer
    {
        /// <summary>
        /// Adds child nodes and returns the same concrete container.
        /// </summary>
        public T Children(params ReadOnlySpan<PromptNode?> children)
        {
            foreach (var child in children)
            {
                if (child is not null) container.Children.Add(child);
            }

            return container;
        }

        /// <summary>
        /// Makes a logical container transparent to its parent's priority scope.
        /// </summary>
        public T WithPassedPriority(bool passPriority = true)
        {
            container.PassPriority = passPriority;
            return container;
        }
    }

    extension(PromptNode node)
    {
        /// <summary>
        /// Wraps a node in an atomic chunk.
        /// </summary>
        public PromptChunk Atomic()
        {
            return new PromptChunk(node);
        }

        /// <summary>
        /// Wraps a node in a subtree with a local token budget.
        /// </summary>
        public PromptTokenLimit LimitTokens(int maxTokens)
        {
            return new PromptTokenLimit(maxTokens, node);
        }
    }

    extension(PromptTextChunk chunk)
    {
        /// <summary>
        /// Configures a text chunk to shorten at Unicode whitespace boundaries.
        /// </summary>
        public PromptTextChunk BreakOnWhitespace()
        {
            chunk.BreakMode = PromptTextBreakMode.Whitespace;
            chunk.Separator = null;
            return chunk;
        }

        /// <summary>
        /// Configures a text chunk to shorten after complete lines.
        /// </summary>
        public PromptTextChunk BreakOnLines()
        {
            chunk.BreakMode = PromptTextBreakMode.Line;
            chunk.Separator = null;
            return chunk;
        }

        /// <summary>
        /// Configures a text chunk to shorten after a specified separator.
        /// </summary>
        public PromptTextChunk BreakOn(string separator)
        {
            chunk.BreakMode = PromptTextBreakMode.Separator;
            chunk.Separator = separator;
            return chunk;
        }

        /// <summary>
        /// Sets a local token ceiling for a text chunk.
        /// </summary>
        public PromptTextChunk WithMaxTokens(int maxTokens)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTokens);
            chunk.MaxTokens = maxTokens;
            return chunk;
        }
    }
}
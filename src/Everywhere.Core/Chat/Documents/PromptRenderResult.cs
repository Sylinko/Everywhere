namespace Everywhere.Chat.Documents;

/// <summary>
/// Describes the rendered model content and which declarative nodes survived its token budget.
/// </summary>
public sealed record PromptRenderResult
{
    /// <summary>
    /// Gets the final rendered content.
    /// </summary>
    public required string Content { get; init; }

    /// <summary>
    /// Gets the estimated token count of <see cref="Content"/>.
    /// </summary>
    public required int TokenCount { get; init; }

    /// <summary>
    /// Gets source nodes represented in the final content, in declaration order.
    /// </summary>
    public required IReadOnlyList<PromptNode> IncludedNodes { get; init; }

    /// <summary>
    /// Gets source nodes omitted from the final content, in declaration order.
    /// </summary>
    public required IReadOnlyList<PromptNode> OmittedNodes { get; init; }

    /// <summary>
    /// Gets text chunks shortened before final priority pruning.
    /// </summary>
    public required IReadOnlyList<PromptTextChunk> TruncatedNodes { get; init; }
}

/// <summary>
/// Indicates that a prompt document cannot be reduced to its requested token budget.
/// </summary>
public sealed class PromptBudgetExceededException : InvalidOperationException
{
    /// <summary>
    /// Creates a prompt budget exception.
    /// </summary>
    public PromptBudgetExceededException(string message) : base(message) { }
}
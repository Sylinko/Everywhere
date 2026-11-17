using Microsoft.Extensions.AI;

namespace Everywhere.Harness;

/// <summary>
/// Represents a transient provider projection of ordered public tool blocks.
/// The error indication is independent of both the tool's text and local invocation exceptions.
/// </summary>
public sealed class ModelToolContent(int capacity, bool isError) : List<AIContent>(capacity)
{
    /// <summary>
    /// Gets whether the tool reported an error outcome.
    /// </summary>
    public bool IsError { get; } = isError;
}

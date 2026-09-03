namespace Everywhere.Automation;

/// <summary>
/// Defines monotonic risk and size limits for one live visual-context Snapshot.
/// </summary>
public sealed record VisualContextSnapshotLimits
{
    /// <summary>
    /// Gets the default Snapshot limits.
    /// </summary>
    public static VisualContextSnapshotLimits Default { get; } = new();

    /// <summary>
    /// Gets the aggregate elapsed-time boundary checked between platform operations.
    /// </summary>
    public TimeSpan MaximumElapsed { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets the maximum number of direct query, Enumerator creation, and Enumerator advancement operations.
    /// </summary>
    public int MaximumPlatformOperations { get; init; } = 4096;

    /// <summary>
    /// Gets the maximum number of distinct elements admitted into the Snapshot.
    /// </summary>
    public int MaximumNodes { get; init; } = 1024;

    /// <summary>
    /// Gets the maximum number of children observed from one parent relation.
    /// </summary>
    public int MaximumChildrenPerNode { get; init; } = 256;

    /// <summary>
    /// Gets the maximum UTF-16 character count retained from one element's text preview.
    /// </summary>
    public int MaximumTextCharactersPerNode { get; init; } = 4096;

    /// <summary>
    /// Gets the maximum aggregate UTF-16 character count retained across the Snapshot.
    /// </summary>
    public int MaximumTotalTextCharacters { get; init; } = 65_536;

    /// <summary>
    /// Gets the maximum number of provider failures tolerated before traversal stops.
    /// </summary>
    public int MaximumProviderFailures { get; init; } = 16;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MaximumElapsed, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumPlatformOperations);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumChildrenPerNode);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumTextCharactersPerNode);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumTotalTextCharacters);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaximumProviderFailures);
    }
}
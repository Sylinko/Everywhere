namespace Everywhere.Chat;

/// <summary>
/// Configures one in-memory projection from a visual-context Snapshot to model-facing prompt content.
/// </summary>
public sealed record VisualContextPromptOptions
{
    /// <summary>
    /// Gets the default prompt projection options.
    /// </summary>
    public static VisualContextPromptOptions Default { get; } = new();

    /// <summary>
    /// Gets the approximate local token budget applied to the complete visual-context projection.
    /// </summary>
    public int TargetTokenBudget { get; init; } = 4_096;

    /// <summary>
    /// Gets the semantic detail level used for container and bounds projection.
    /// </summary>
    public VisualContextDetailLevel DetailLevel { get; init; } = VisualContextDetailLevel.Compact;

    /// <summary>
    /// Gets the minimum number of adjacent passive leaf nodes required to form a Composite.
    /// </summary>
    public int MinimumCompositeMemberCount { get; init; } = 2;

    /// <summary>
    /// Gets the maximum number of UTF-16 characters retained in one scalar attribute.
    /// </summary>
    public int MaximumScalarCharacters { get; init; } = 1_024;

    /// <summary>
    /// Gets the maximum number of UTF-16 characters retained in one Composite preview.
    /// </summary>
    public int MaximumCompositePreviewCharacters { get; init; } = 4_096;

    /// <summary>
    /// Gets bounded operation-level status appended to Snapshot and prompt-budget status on the visual-context root.
    /// </summary>
    public IReadOnlyList<string> AdditionalStatus { get; init; } = [];

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(TargetTokenBudget);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumCompositeMemberCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumScalarCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(MaximumCompositePreviewCharacters);
    }
}
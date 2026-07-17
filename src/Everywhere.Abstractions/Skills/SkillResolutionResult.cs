namespace Everywhere.Skills;

/// <summary>
/// Result of resolving a complete skill reference such as <c>skill://builtin.officecli</c>.
/// </summary>
public sealed record SkillResolutionResult
{
    /// <summary>
    /// Original reference string before normalization.
    /// </summary>
    public required string Reference { get; init; }

    /// <summary>
    /// Selected skill, or null when no installed skill matches the reference.
    /// </summary>
    public SkillDescriptor? Skill { get; init; }
}

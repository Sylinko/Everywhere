namespace Everywhere.Skills;

/// <summary>
/// Result of resolving a skill reference such as <c>skill://deepwiki</c>.
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

    /// <summary>
    /// Matching candidates in first-wins order.
    /// </summary>
    /// <remarks>
    /// Short references such as <c>skill://deepwiki</c> may match <c>agents.deepwiki</c> and <c>codex.deepwiki</c>.
    /// </remarks>
    public IReadOnlyList<SkillDescriptor> Candidates { get; init; } = [];

    /// <summary>
    /// True when a short reference matched more than one installed skill.
    /// </summary>
    public bool IsAmbiguous => Candidates.Count > 1;
}

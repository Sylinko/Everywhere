namespace Everywhere.StrategyEngine;

/// <summary>
/// Authoring-time source reference used by the <c>from</c> field.
/// </summary>
public sealed record StrategyFromReference
{
    /// <summary>
    /// Raw reference value such as <c>./base.strategy.md</c>, <c>skill://writing.polite</c>, or a URL.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>
    /// Resolver hint. <see cref="StrategyFromReferenceKind.Auto"/> lets resolvers infer from the source shape.
    /// </summary>
    public StrategyFromReferenceKind Kind { get; init; } = StrategyFromReferenceKind.Auto;
}

/// <summary>
/// Supported authoring-time source categories for <c>from</c>.
/// </summary>
public enum StrategyFromReferenceKind
{
    Auto,
    Skill,
    Strategy,
    Markdown,
    Url
}

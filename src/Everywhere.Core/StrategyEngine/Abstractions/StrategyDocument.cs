namespace Everywhere.StrategyEngine;

/// <summary>
/// Parsed strategy source before normalization into a runtime <see cref="Strategy"/>.
/// </summary>
public sealed record StrategyDocument
{
    /// <summary>
    /// Source that produced this parsed document.
    /// </summary>
    public required StrategySource Source { get; init; }

    /// <summary>
    /// Schema selected from frontmatter, or the parser default when omitted.
    /// </summary>
    public required string Schema { get; init; }

    /// <summary>
    /// Versioned authoring model for <see cref="Schema"/>.
    /// </summary>
    /// <remarks>
    /// For v1 this is <see cref="StrategyDefinitionV1"/>. The type is object so future schema versions can coexist.
    /// </remarks>
    public required object Definition { get; init; }

    /// <summary>
    /// Markdown body after frontmatter with normalized line endings.
    /// </summary>
    public string? Body { get; init; }

    /// <summary>
    /// True when the document explicitly contains a body section after the closing frontmatter fence.
    /// </summary>
    /// <remarks>
    /// Normalization uses this to decide whether <c>from</c> should inherit or replace the source body.
    /// </remarks>
    public bool HasBodySection { get; init; }

    /// <summary>
    /// Frontmatter text after line-ending normalization, without the surrounding fences.
    /// </summary>
    public string? RawFrontmatter { get; init; }

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

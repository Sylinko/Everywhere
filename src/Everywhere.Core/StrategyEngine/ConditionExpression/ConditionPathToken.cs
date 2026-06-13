namespace Everywhere.StrategyEngine.ConditionExpression;

/// <summary>
/// Token kind for one path segment after parsing property/index syntax.
/// </summary>
internal enum ConditionPathTokenKind
{
    Property,
    Index,
    ReverseIndex,
    Range
}

/// <summary>
/// Parsed token from a single condition path segment.
/// </summary>
/// <remarks>
/// Segment tokenization keeps index/range operations attached to the property they were authored on. For
/// example <c>files[^1]</c> produces a property token for <c>files</c> and a reverse-index token.
/// </remarks>
internal sealed record ConditionPathToken
{
    public required ConditionPathTokenKind Kind { get; init; }

    /// <summary>
    /// Property name for <see cref="ConditionPathTokenKind.Property"/> tokens.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Zero-based, negative, or from-end index depending on <see cref="Kind"/>.
    /// </summary>
    public int? Index { get; init; }

    /// <summary>
    /// Inclusive range start for <see cref="ConditionPathTokenKind.Range"/> tokens; null means open-ended.
    /// </summary>
    public ConditionRangeBound? Start { get; init; }

    /// <summary>
    /// Exclusive range end for <see cref="ConditionPathTokenKind.Range"/> tokens; null means open-ended.
    /// </summary>
    public ConditionRangeBound? End { get; init; }

    /// <summary>
    /// Canonical token text used when reconstructing normalized paths.
    /// </summary>
    /// <example><c>[^1]</c>, <c>[0..3]</c>, or <c>files</c>.</example>
    public required string Text { get; init; }
}

/// <summary>
/// One bound of an index range.
/// </summary>
/// <remarks>
/// From-end bounds use the same surface syntax as C# index-from-end, for example <c>^1</c>.
/// </remarks>
internal sealed record ConditionRangeBound
{
    /// <summary>
    /// Numeric bound value before translating from-end indexes to absolute positions.
    /// </summary>
    public required int Value { get; init; }

    /// <summary>
    /// True for bounds authored with <c>^</c>, such as <c>^1</c>.
    /// </summary>
    public bool IsFromEnd { get; init; }

    /// <summary>
    /// Canonical bound text preserved for path reconstruction.
    /// </summary>
    public required string Text { get; init; }
}

/// <summary>
/// Result of tokenizing one author-facing condition key.
/// </summary>
/// <remarks>
/// <see cref="CanonicalSegments"/> is used by the frontend to normalize dotted keys into nested mappings, while
/// <see cref="Tokens"/> preserves index/range operations for semantic binding.
/// </remarks>
internal sealed record ConditionPathTokenization(
    IReadOnlyList<string> CanonicalSegments,
    IReadOnlyList<ConditionPathToken> Tokens
);

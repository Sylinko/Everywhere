namespace Everywhere.StrategyEngine;

/// <summary>
/// Structured diagnostic emitted while loading, normalizing, matching, or executing strategies.
/// </summary>
public sealed record StrategyDiagnostic
{
    public required StrategyDiagnosticSeverity Severity { get; init; }

    /// <summary>
    /// Stable diagnostic identifier, for example <c>strategy.invalid_duration</c>.
    /// </summary>
    public required string Code { get; init; }

    /// <summary>
    /// Localizable message key. Use <c>DirectResourceKey</c> for diagnostics that are not yet localized.
    /// </summary>
    public IDynamicResourceKey? MessageKey { get; init; }

    /// <summary>
    /// Field path, condition path, or source path associated with the diagnostic.
    /// </summary>
    /// <remarks>
    /// Examples: <c>options.matchingTimeout</c>, <c>when.any[0]</c>, or a file path.
    /// </remarks>
    public string? Path { get; init; }

    /// <summary>
    /// Provider namespace that emitted or owns the diagnostic.
    /// </summary>
    public string? ProviderId { get; init; }

    /// <summary>
    /// Optional duration used for slow-match or timeout diagnostics.
    /// </summary>
    public TimeSpan? Duration { get; init; }

    /// <summary>
    /// Captured exception for logs; UI should prefer <see cref="MessageKey"/>.
    /// </summary>
    public Exception? Exception { get; init; }
}

/// <summary>
/// Diagnostic severity used by strategy parsing, normalization, matching, and execution.
/// </summary>
public enum StrategyDiagnosticSeverity
{
    Info,
    Warning,
    Error
}
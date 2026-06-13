namespace Everywhere.StrategyEngine;

/// <summary>
/// Resolves public Strategy DSL paths such as <c>attachments.selection.text</c>.
/// </summary>
public interface IStrategyPathResolver
{
    /// <summary>
    /// True when this resolver owns the supplied path.
    /// </summary>
    bool CanResolve(string path);

    /// <summary>
    /// Resolves a path against the current strategy context.
    /// </summary>
    StrategyPathResolution Resolve(StrategyContext context, string path);
}

/// <summary>
/// Result of resolving a Strategy DSL path.
/// </summary>
public sealed record StrategyPathResolution
{
    public static StrategyPathResolution Missing(string path, StrategyDiagnostic? diagnostic = null) => new()
    {
        Path = path,
        IsResolved = false,
        Diagnostics = diagnostic is null ? [] : [diagnostic]
    };

    public static StrategyPathResolution Resolved(string path, object? value) => new()
    {
        Path = path,
        IsResolved = value is not null,
        Value = value
    };

    public required string Path { get; init; }

    /// <summary>
    /// False when the path is known but current data is unavailable.
    /// </summary>
    public required bool IsResolved { get; init; }

    public object? Value { get; init; }

    public IReadOnlyList<StrategyDiagnostic> Diagnostics { get; init; } = [];
}

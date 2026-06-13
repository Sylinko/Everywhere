namespace Everywhere.StrategyEngine;

/// <summary>
/// Context passed to strategy preprocessors before prompt rendering.
/// </summary>
public sealed record StrategyExecutionContext
{
    public required Strategy Strategy { get; init; }

    /// <summary>
    /// Base context captured from attachments, selection, active app, and window data.
    /// </summary>
    public required StrategyContext StrategyContext { get; init; }

    /// <summary>
    /// User-entered argument text available to preprocessors.
    /// </summary>
    public string? UserInput { get; init; }

    /// <summary>
    /// Extra context already collected for matching or execution.
    /// </summary>
    public ExtraContextSnapshot? ExtraContext { get; init; }

    public CancellationToken CancellationToken { get; init; }
}

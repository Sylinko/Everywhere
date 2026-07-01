namespace Everywhere.StrategyEngine;

/// <summary>
/// A preprocessor invoked before executing a strategy to retrieve or modify data
/// for populating template variables or validating contexts.
/// </summary>
public interface IStrategyPreprocessor
{
    /// <summary>
    /// Unique identifier for this preprocessor. Matches string in `<see cref="Strategy.Preprocessors"/>`.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Processes the current strategy context and returns interpolation variables.
    /// </summary>
    /// <param name="context">The strategy evaluation context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing key-value string variables.</returns>
    Task<PreprocessorResult> ProcessAsync(StrategyContext context, CancellationToken cancellationToken = default);
}
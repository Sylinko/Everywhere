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
    /// Processes the current strategy execution and returns interpolation variables.
    /// </summary>
    /// <param name="context">The strategy execution context.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing key-value string variables.</returns>
    Task<PreprocessorResult> ProcessAsync(StrategyExecutionContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Registry for trusted strategy preprocessors referenced by strategy ID.
/// </summary>
public interface IStrategyPreprocessorRegistry
{
    bool TryGet(string id, out IStrategyPreprocessor preprocessor);

    IReadOnlyList<IStrategyPreprocessor> GetAll();
}

/// <summary>
/// Executes the preprocessors declared by a strategy before prompt rendering.
/// </summary>
public interface IStrategyPreprocessorExecutor
{
    Task<StrategyPreprocessorExecutionResult> ExecuteAsync(
        StrategyExecutionContext context,
        CancellationToken cancellationToken = default);
}

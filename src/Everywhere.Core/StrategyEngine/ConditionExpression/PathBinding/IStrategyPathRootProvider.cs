namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Provides a public root available to condition paths.
/// </summary>
/// <remarks>
/// Root names are author-facing DSL names such as <c>attachments</c>, <c>activeProcess</c>, <c>environment</c>,
/// or the deferred <c>extra</c> root.
/// </remarks>
internal interface IStrategyPathRootProvider
{
    /// <summary>
    /// Public root name accepted at the start of a condition path.
    /// </summary>
    string RootName { get; }

    /// <summary>
    /// Static CLR shape exposed at this root for binding.
    /// </summary>
    Type ValueType { get; }

    /// <summary>
    /// True when the root may bind before runtime data is available.
    /// </summary>
    /// <remarks>
    /// Deferred roots, currently <c>extra</c>, bind as unknown and are collected or reported at evaluation time.
    /// </remarks>
    bool IsDeferred { get; }

    /// <summary>
    /// Reads the root value from a runtime strategy context.
    /// </summary>
    /// <remarks>
    /// Deferred roots may return null here and be resolved by specialized runtime services instead.
    /// </remarks>
    object? GetValue(StrategyContext context);
}
namespace Everywhere.StrategyEngine;

/// <summary>
/// Services available while loading and normalizing strategy documents.
/// </summary>
public sealed record StrategyLoadContext
{
    /// <summary>
    /// Resolvers used for authoring-time <c>from</c> references.
    /// </summary>
    public IReadOnlyList<IStrategySourceResolver> SourceResolvers { get; init; } = [];
}

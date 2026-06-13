namespace Everywhere.StrategyEngine;

/// <summary>
/// Provides a collection of strategies.
/// Implementations can load strategies from files, databases, or memory.
/// </summary>
public interface IStrategyProvider
{
    /// <summary>
    /// The namespace this provider contributes to (e.g., "builtin", "user").
    /// </summary>
    string Namespace { get; }

    /// <summary>
    /// Gets strategies currently available from this provider.
    /// </summary>
    /// <remarks>
    /// User enablement and provider-specific filtering should be applied here before strategies reach the registry.
    /// </remarks>
    IEnumerable<Strategy> GetStrategies();
}

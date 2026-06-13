namespace Everywhere.StrategyEngine.ConditionExpression.PathBinding;

/// <summary>
/// Resolves public DSL property names to runtime accessors.
/// </summary>
/// <remarks>
/// Implementations should honor the DSL schema, not arbitrary CLR member names. The JSON-backed implementation
/// uses <c>JsonPropertyName</c>, generated metadata, and <c>JsonIgnore</c>.
/// </remarks>
internal interface IStrategyPathAccessorProvider
{
    /// <summary>
    /// Attempts to resolve a public property name on the specified static type.
    /// </summary>
    bool TryGetAccessor(Type type, string publicName, out StrategyPathAccessor accessor);

    /// <summary>
    /// Returns known public names for near-match diagnostics.
    /// </summary>
    IReadOnlyList<string> GetKnownNames(Type type);
}
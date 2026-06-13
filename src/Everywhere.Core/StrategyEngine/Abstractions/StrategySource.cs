namespace Everywhere.StrategyEngine;

/// <summary>
/// Describes where a strategy or included strategy source came from.
/// </summary>
public sealed record StrategySource
{
    /// <summary>
    /// Sentinel source used by legacy or in-memory strategies that have not been assigned a provider.
    /// </summary>
    public static StrategySource Unknown { get; } = new()
    {
        ProviderId = "unknown",
        Location = new Uri("strategy://unknown")
    };

    /// <summary>
    /// Creates a stable synthetic URI for strategies supplied directly by a provider.
    /// </summary>
    public static StrategySource FromProvider(string providerId, string id) => new()
    {
        ProviderId = providerId,
        Location = new Uri($"strategy://{Uri.EscapeDataString(providerId)}/{Uri.EscapeDataString(id)}"),
        IsBuiltin = providerId.Equals("builtin", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>
    /// Namespace of the provider that owns this source, for example <c>builtin</c> or <c>user</c>.
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// File, resource, strategy, skill, or URL location used for diagnostics and editor navigation.
    /// </summary>
    public required Uri Location { get; init; }

    /// <summary>
    /// Optional content hash used to detect stale cached parses.
    /// </summary>
    public string? SourceHash { get; init; }

    /// <summary>
    /// Whether this source was supplied by the builtin provider namespace.
    /// </summary>
    public bool IsBuiltin { get; init; }
}

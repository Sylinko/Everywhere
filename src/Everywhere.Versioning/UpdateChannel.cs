namespace Everywhere.Common;

/// <summary>
/// Identifies application update feeds. Numeric values are persisted settings, not version precedence.
/// </summary>
public enum UpdateChannel
{
    /// <summary>
    /// Represents an unrecognized label or the application's automatic channel selection.
    /// </summary>
    Unknown = 0,

    /// <summary>
    /// Selects development snapshots.
    /// </summary>
    Canary = 1,

    /// <summary>
    /// Selects releases without a prerelease label.
    /// </summary>
    Stable = 99
}
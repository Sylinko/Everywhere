namespace Everywhere.ProcessIsolation.Roles;

/// <summary>
/// Identifies the responsibility owned by an Everywhere process.
/// </summary>
public enum ProcessRole
{
    /// <summary>The normal UI process that owns the user-facing application.</summary>
    Main = 0,

    /// <summary>The process that owns global keyboard and mouse monitoring.</summary>
    Input = 1,

    /// <summary>The process that owns accessibility and visual-tree automation.</summary>
    Automation = 2
}
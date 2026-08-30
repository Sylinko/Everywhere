namespace Everywhere.Automation.Testing;

/// <summary>
/// Describes platform-neutral state flags for declarative scenario controls.
/// </summary>
[Flags]
public enum ScenarioControlStates
{
    None = 0,
    Offscreen = 1 << 0,
    Disabled = 1 << 1,
    Focused = 1 << 2,
    Selected = 1 << 3,
    ReadOnly = 1 << 4,
    Password = 1 << 5,
}

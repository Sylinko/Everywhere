namespace Everywhere.Automation;

/// <summary>
/// Identifies operations that may be requested for an Agent-visible visual target.
/// </summary>
[Flags]
public enum VisualTargetCapabilities
{
    None = 0,
    Inspect = 1 << 0,
    Navigate = 1 << 1,
    Expand = 1 << 2,
    ReadContent = 1 << 3,
    Find = 1 << 4,
    Invoke = 1 << 5,
    SetText = 1 << 6,
    SendKeyGesture = 1 << 7,
    Capture = 1 << 8,
    Focus = 1 << 9,
}

/// <summary>
/// Represents one target that was exposed to the Agent by a visual-context projection.
/// </summary>
public abstract class VisualTarget
{
    /// <summary>
    /// Gets the operations supported by this target.
    /// </summary>
    public required VisualTargetCapabilities Capabilities { get; init; }

    /// <summary>
    /// Gets bounded explanations for incomplete or degraded target state.
    /// </summary>
    public IReadOnlyList<string> Status { get; init; } = [];
}

/// <summary>
/// Represents exactly one live platform element exposed to the Agent.
/// </summary>
public sealed class ElementTarget : VisualTarget
{
    /// <summary>
    /// Gets the Context-owned platform element used by later queries and validated actions.
    /// </summary>
    public required VisualElement Element { get; init; }
}
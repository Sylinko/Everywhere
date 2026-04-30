namespace Everywhere.VisualContext.Testing;

/// <summary>
/// Represents one declarative control in a generated visual scenario.
/// </summary>
/// <remarks>
/// Controls describe logical UI rather than a platform object. Backends may project the same
/// control into a mock element, native control, or browser DOM node.
/// </remarks>
public abstract class VisualControl
{
    /// <summary>
    /// Gets the semantic kind of the control.
    /// </summary>
    public abstract ScenarioControlKind Kind { get; }

    /// <summary>
    /// Gets an optional stable scenario-local key.
    /// </summary>
    public string? Key { get; init; }

    /// <summary>
    /// Gets the accessible name exposed by a backend.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the textual content exposed by a backend.
    /// </summary>
    public string? TextContent { get; init; }

    /// <summary>
    /// Gets the normalized control states.
    /// </summary>
    public ScenarioControlStates States { get; init; }

    /// <summary>
    /// Gets whether this control is an intended core element for a scenario assertion.
    /// </summary>
    public bool IsCore { get; init; }

    /// <summary>
    /// Gets the logical child count without materializing child controls.
    /// </summary>
    public virtual int ChildCount => 0;

    /// <summary>
    /// Creates or retrieves the logical child at the specified zero-based index.
    /// </summary>
    public virtual VisualControl GetChild(int index) => throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>
/// Provides indexed child access for a fixed, declaratively supplied child list.
/// </summary>
public abstract class FixedContainerControl : VisualControl
{
    public override int ChildCount => _children.Count;

    private readonly IReadOnlyList<VisualControl> _children;

    /// <summary>
    /// Initializes a fixed container from its child controls.
    /// </summary>
    protected FixedContainerControl(params IReadOnlyList<VisualControl> children) => _children = children;

    /// <inheritdoc />
    public override VisualControl GetChild(int index) => _children[index];
}

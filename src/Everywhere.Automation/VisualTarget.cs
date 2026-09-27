namespace Everywhere.Automation;

/// <summary>
/// Represents one target that was exposed to the Agent by a visual-context projection.
/// </summary>
public abstract class VisualTarget
{
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

/// <summary>
/// Identifies the live scalar field that contributes one member to a Composite text stream.
/// </summary>
public enum CompositePartContentSource
{
    /// <summary>The member contributes its live text value.</summary>
    Text,

    /// <summary>The member contributes its live accessibility name.</summary>
    Name,
}

/// <summary>
/// Contains one ordered source member retained by a logical <see cref="CompositeTarget" />.
/// </summary>
public sealed record CompositePart
{
    /// <summary>
    /// Gets the live source element used for later bounded inspection and target publication.
    /// </summary>
    public required VisualElement Element { get; init; }

    /// <summary>Gets the live scalar field selected when this member was projected.</summary>
    public required CompositePartContentSource ContentSource { get; init; }
}

/// <summary>
/// Represents one Agent-addressable logical projection over several ordered visual elements.
/// </summary>
/// <remarks>
/// A Composite exposes a live logical text stream after structural compression but is not a platform element and cannot receive platform operations.
/// </remarks>
public sealed class CompositeTarget : VisualTarget
{
    /// <summary>
    /// Gets the ordered live source members represented by this Composite.
    /// </summary>
    public required IReadOnlyList<CompositePart> Parts { get; init; }
}
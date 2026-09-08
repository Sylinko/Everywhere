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
/// Contains one ordered source member retained by a logical <see cref="CompositeTarget" />.
/// </summary>
public sealed record CompositePart
{
    /// <summary>
    /// Gets the live source element used for later bounded inspection and target publication.
    /// </summary>
    public required VisualElement Element { get; init; }

    /// <summary>
    /// Gets the bounded scalar facts copied while the source Composite was projected.
    /// </summary>
    public required VisualElementSnapshot Snapshot { get; init; }

}

/// <summary>
/// Represents one Agent-addressable logical projection over several ordered visual elements.
/// </summary>
/// <remarks>
/// A Composite preserves queryability after structural compression but is not a platform element and cannot receive platform actions.
/// </remarks>
public sealed class CompositeTarget : VisualTarget
{
    /// <summary>
    /// Gets the ordered bounded source members represented by this Composite.
    /// </summary>
    public required IReadOnlyList<CompositePart> Parts { get; init; }
}
namespace Everywhere.Automation;

/// <summary>
/// Defines the relations that visual-context snapshot traversal may observe.
/// </summary>
[Flags]
public enum VisualContextTraverseDirections
{
    /// <summary>
    /// Represents the caller-supplied core elements without enabling an additional relation.
    /// </summary>
    Core = 0,

    /// <summary>
    /// Enables traversal toward an element's parent.
    /// </summary>
    Parent = 0x1,

    /// <summary>
    /// Enables traversal toward an element's previous sibling.
    /// </summary>
    PreviousSibling = 0x2,

    /// <summary>
    /// Enables traversal toward an element's next sibling.
    /// </summary>
    NextSibling = 0x4,

    /// <summary>
    /// Enables traversal toward an element's children.
    /// </summary>
    Child = 0x8,

    /// <summary>
    /// Enables every structural relation.
    /// </summary>
    All = Parent | PreviousSibling | NextSibling | Child
}
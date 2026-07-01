namespace Everywhere.Chat;

/// <summary>
/// Defines the direction of traversal in the visual element tree.
/// It determines how a queued node is expanded.
/// </summary>
[Flags]
public enum VisualContextTraverseDirections
{
    /// <summary>
    /// Core elements
    /// </summary>
    Core = 0,

    /// <summary>
    /// parent, previous sibling, next sibling
    /// </summary>
    Parent = 0x1,

    /// <summary>
    /// previous sibling, child
    /// </summary>
    PreviousSibling = 0x2,

    /// <summary>
    /// next sibling, child
    /// </summary>
    NextSibling = 0x4,

    /// <summary>
    /// next child, child
    /// </summary>
    Child = 0x8,

    All = Parent | PreviousSibling | NextSibling | Child
}
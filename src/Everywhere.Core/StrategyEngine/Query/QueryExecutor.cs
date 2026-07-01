using Everywhere.Interop;

namespace Everywhere.StrategyEngine.Query;

/// <summary>
/// Executes visual element queries against the visual tree.
/// Supports ancestor traversal, descendant search, and sibling navigation.
/// </summary>
public sealed class QueryExecutor
{
    /// <summary>
    /// Maximum depth for descendant traversal.
    /// </summary>
    public int MaxDescendantDepth { get; init; } = 10;

    /// <summary>
    /// Maximum number of siblings to check in each direction.
    /// </summary>
    public int MaxSiblings { get; init; } = 20;

    /// <summary>
    /// Finds ancestors matching the selector, starting from the given element.
    /// </summary>
    public static IEnumerable<IVisualElement> FindAncestors(
        IVisualElement start,
        ResilientSelector selector,
        bool includeSelf = false)
    {
        var current = includeSelf ? start : start.Parent;

        while (current is not null)
        {
            if (selector.Matches(current))
            {
                yield return current;
            }

            current = current.Parent;
        }
    }

    /// <summary>
    /// Finds the first ancestor matching the selector.
    /// </summary>
    public static IVisualElement? FindFirstAncestor(
        IVisualElement start,
        ResilientSelector selector,
        bool includeSelf = false)
    {
        return FindAncestors(start, selector, includeSelf).FirstOrDefault();
    }

    /// <summary>
    /// Traverses up to a specific element type (e.g., TopLevel or Screen).
    /// </summary>
    public static IVisualElement? FindAncestorOfType(
        IVisualElement start,
        VisualElementType type)
    {
        return FindFirstAncestor(start, ResilientSelector.ForType(type));
    }

    /// <summary>
    /// Finds descendants matching the selector using breadth-first search.
    /// Uses depth limiting to avoid full tree traversal.
    /// </summary>
    public IEnumerable<IVisualElement> FindDescendants(
        IVisualElement start,
        ResilientSelector selector,
        int maxResults = -1)
    {
        var resultCount = 0;
        var queue = new Queue<(IVisualElement Element, int Depth)>();

        // Add direct children to queue
        foreach (var child in start.Children)
        {
            queue.Enqueue((child, 1));
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (selector.Matches(current))
            {
                yield return current;
                resultCount++;

                if (maxResults > 0 && resultCount >= maxResults)
                {
                    yield break;
                }
            }

            // Only go deeper if we haven't hit the limit
            if (depth < MaxDescendantDepth)
            {
                foreach (var child in current.Children)
                {
                    queue.Enqueue((child, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// Finds the first descendant matching the selector.
    /// </summary>
    public IVisualElement? FindFirstDescendant(
        IVisualElement start,
        ResilientSelector selector)
    {
        return FindDescendants(start, selector, maxResults: 1).FirstOrDefault();
    }

    /// <summary>
    /// Finds direct children matching the selector.
    /// </summary>
    public static IEnumerable<IVisualElement> FindDirectChildren(
        IVisualElement parent,
        ResilientSelector selector)
    {
        return parent.Children.Where(selector.Matches);
    }

    /// <summary>
    /// Finds siblings matching the selector.
    /// </summary>
    public IEnumerable<IVisualElement> FindSiblings(
        IVisualElement element,
        ResilientSelector selector,
        SiblingDirection direction = SiblingDirection.Both)
    {
        using var accessor = element.SiblingAccessor;

        if (direction is SiblingDirection.Forward or SiblingDirection.Both)
        {
            var count = 0;
            using var forward = accessor.ForwardEnumerator;
            while (forward.MoveNext() && count < MaxSiblings)
            {
                if (selector.Matches(forward.Current))
                {
                    yield return forward.Current;
                }

                count++;
            }
        }

        if (direction is SiblingDirection.Backward or SiblingDirection.Both)
        {
            var count = 0;
            using var backward = accessor.BackwardEnumerator;
            while (backward.MoveNext() && count < MaxSiblings)
            {
                if (selector.Matches(backward.Current))
                {
                    yield return backward.Current;
                }

                count++;
            }
        }
    }

    /// <summary>
    /// Executes a cross-path query: goes up to an ancestor, then down to find targets.
    /// This is the key operation for checking elements in sibling subtrees.
    /// </summary>
    /// <param name="start">Starting element (usually from an attachment).</param>
    /// <param name="ancestorSelector">Selector for the common ancestor to find.</param>
    /// <param name="targetSelector">Selector for the target elements to find.</param>
    /// <returns>Matching elements, or empty if ancestor not found.</returns>
    public IEnumerable<IVisualElement> CrossPathQuery(
        IVisualElement start,
        ResilientSelector ancestorSelector,
        ResilientSelector targetSelector)
    {
        // Step 1: Find the ancestor
        var ancestor = FindFirstAncestor(start, ancestorSelector, includeSelf: true);
        if (ancestor is null)
        {
            return [];
        }

        // Step 2: Search descendants from the ancestor
        return FindDescendants(ancestor, targetSelector);
    }

    /// <summary>
    /// Checks if any element exists matching a cross-path query.
    /// More efficient than getting all results when you just need existence check.
    /// </summary>
    public bool ExistsCrossPath(
        IVisualElement start,
        ResilientSelector ancestorSelector,
        ResilientSelector targetSelector)
    {
        var ancestor = FindFirstAncestor(start, ancestorSelector, includeSelf: true);
        if (ancestor is null)
        {
            return false;
        }

        return FindDescendants(ancestor, targetSelector, maxResults: 1).Any();
    }
}

/// <summary>
/// Direction for sibling traversal.
/// </summary>
public enum SiblingDirection
{
    Forward,
    Backward,
    Both
}

namespace Everywhere.Web;

/// <summary>
/// A normalized search result shared by the web tool's display and prompt projections.
/// Providers may return a snippet without a title or a link.
/// </summary>
public sealed record WebSearchResult(string Value)
{
    /// <summary>
    /// Gets the title of the search result, if available.
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the link of the search result, if available.
    /// </summary>
    public string? Link { get; init; }
}
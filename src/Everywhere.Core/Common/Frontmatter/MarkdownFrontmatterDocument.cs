namespace Everywhere.Common.Frontmatter;

internal sealed record MarkdownFrontmatterDocument(
    string Content,
    string Body,
    string? RawFrontmatter,
    bool HasFrontmatter,
    bool HasBodySection);

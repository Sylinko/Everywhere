namespace Everywhere.Common.Frontmatter;

internal static class MarkdownFrontmatterParser
{
    public static MarkdownFrontmatterDocument Parse(string content)
    {
        var normalizedContent = NormalizeLineEndings(content);
        if (!TryReadFrontmatter(normalizedContent, out var frontmatter, out var body))
        {
            return new MarkdownFrontmatterDocument(normalizedContent, normalizedContent, null, false, true);
        }

        return new MarkdownFrontmatterDocument(normalizedContent, body, frontmatter, true, body.Length > 0);
    }

    private static bool TryReadFrontmatter(string content, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = content;

        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            return false;
        }

        var firstLineEnd = content.IndexOf('\n');
        if (firstLineEnd < 0) return false;

        var searchStart = firstLineEnd + 1;
        while (true)
        {
            var separatorIndex = content.IndexOf("\n---", searchStart, StringComparison.Ordinal);
            if (separatorIndex < 0) return false;

            var separatorEnd = separatorIndex + "\n---".Length;
            if (separatorEnd == content.Length || content[separatorEnd] == '\n')
            {
                if (separatorEnd < content.Length && content[separatorEnd] == '\n') separatorEnd++;

                frontmatter = content[searchStart..separatorIndex].Trim('\n');
                body = content[Math.Min(separatorEnd, content.Length)..];
                return true;
            }

            searchStart = separatorEnd;
        }
    }

    public static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');
}

namespace Everywhere.Common.Frontmatter;

internal sealed record FrontmatterDiagnostic(
    string Id,
    string Message,
    string? Path = null,
    Exception? Exception = null);

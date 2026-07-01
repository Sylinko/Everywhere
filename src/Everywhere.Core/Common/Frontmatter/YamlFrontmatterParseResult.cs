namespace Everywhere.Common.Frontmatter;

internal sealed record YamlFrontmatterParseResult(
    IReadOnlyDictionary<string, object?>? Values,
    IReadOnlyList<FrontmatterDiagnostic> Diagnostics);

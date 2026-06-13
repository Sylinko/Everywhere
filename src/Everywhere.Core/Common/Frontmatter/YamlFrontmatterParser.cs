using SharpYaml;

namespace Everywhere.Common.Frontmatter;

internal static class YamlFrontmatterParser
{
    private static readonly YamlSerializerOptions YamlOptions = YamlSerializerOptions.Default with
    {
        PropertyNameCaseInsensitive = true
    };

    public static YamlFrontmatterParseResult ParseMapping(string frontmatter)
    {
        try
        {
            var value = YamlSerializer.Deserialize<object>(frontmatter, YamlOptions);
            if (value is null)
            {
                return new YamlFrontmatterParseResult(
                    new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase),
                    []);
            }

            if (value is IReadOnlyDictionary<string, object?> typed)
            {
                return new YamlFrontmatterParseResult(
                    new Dictionary<string, object?>(typed, StringComparer.OrdinalIgnoreCase),
                    []);
            }

            return new YamlFrontmatterParseResult(
                null,
                [new FrontmatterDiagnostic("frontmatter.invalid_yaml", "Frontmatter must be a YAML mapping.")]);
        }
        catch (YamlException ex)
        {
            return new YamlFrontmatterParseResult(
                null,
                [new FrontmatterDiagnostic("frontmatter.invalid_yaml", "Frontmatter contains invalid YAML.", Exception: ex)]);
        }
    }
}

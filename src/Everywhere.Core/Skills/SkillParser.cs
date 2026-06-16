using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Avalonia.Controls.Notifications;
using Everywhere.Common.Frontmatter;
using ZLinq;

namespace Everywhere.Skills;

internal static partial class SkillParser
{
    private static readonly ImmutableHashSet<string> OfficialSkillKeys = ImmutableHashSet.Create<string>(
        StringComparer.OrdinalIgnoreCase,
        "name",
        "description");

    public static SkillParseResult Parse(string filePath, string folderName, string content)
    {
        string? frontmatterName = null;
        string? frontmatterDescription = null;
        var (_, body, rawFrontmatter, _, _) = MarkdownFrontmatterParser.Parse(content);
        IReadOnlyDictionary<string, string> metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var diagnostics = new List<SkillDiagnostic>();
        if (rawFrontmatter is not null)
        {
            var parseResult = YamlFrontmatterParser.ParseMapping(rawFrontmatter);
            diagnostics.AddRange(parseResult.Diagnostics.Select(ConvertDiagnostic));
            if (parseResult.Values is { } values)
            {
                var valueDiagnostics = new List<FrontmatterDiagnostic>();
                frontmatterName = YamlValueReader.ReadString(values, "name", valueDiagnostics);
                frontmatterDescription = YamlValueReader.ReadString(values, "description", valueDiagnostics);
                diagnostics.AddRange(valueDiagnostics.Select(ConvertDiagnostic));
                metadata = YamlValueReader.FlattenScalarMetadata(values, OfficialSkillKeys);
            }
        }

        frontmatterName = frontmatterName.NullIfWhiteSpace();
        frontmatterDescription = frontmatterDescription.NullIfWhiteSpace();
        var h1 = FindFirstHeading(body).NullIfWhiteSpace();
        var paragraph = FindFirstMeaningfulParagraph(body).NullIfWhiteSpace();
        var directoryName = folderName.NullIfWhiteSpace() ??
            new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty).Name.NullIfWhiteSpace();
        diagnostics.AddRange(CreateDiagnostics(frontmatterName, frontmatterDescription, directoryName));

        return new SkillParseResult(
            frontmatterName,
            frontmatterDescription,
            h1,
            paragraph,
            directoryName,
            body,
            metadata,
            diagnostics);
    }

    private static List<SkillDiagnostic> CreateDiagnostics(string? name, string? description, string? directoryName)
    {
        var diagnostics = new List<SkillDiagnostic>();
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(
                new SkillDiagnostic(
                    "skill.missing_name",
                    new DirectLocaleKey("SKILL.md is missing required frontmatter field 'name'."),
                    NotificationType.Error));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostics.Add(
                new SkillDiagnostic(
                    "skill.missing_description",
                    new DirectLocaleKey("SKILL.md is missing required frontmatter field 'description'."),
                    NotificationType.Error));
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(directoryName) &&
            !name.Equals(directoryName, StringComparison.Ordinal))
        {
            diagnostics.Add(
                new SkillDiagnostic(
                    "skill.name_folder_mismatch",
                    new DirectLocaleKey("Skill name does not match the folder name; the folder name is used for the skill ID."),
                    NotificationType.Warning));
        }

        return diagnostics;
    }

    private static string? FindFirstHeading(string body) => body
        .Split('\n')
        .AsValueEnumerable()
        .Select(line => HeadingRegex().Match(line))
        .Where(match => match.Success)
        .Select(match => match.Groups["title"].Value.Trim()).FirstOrDefault();

    private static string? FindFirstMeaningfulParagraph(string body)
    {
        var lines = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 ||
                line.StartsWith('#') ||
                line.StartsWith("```", StringComparison.Ordinal) ||
                line.StartsWith("---", StringComparison.Ordinal))
            {
                if (lines.Count > 0) break;
                continue;
            }

            lines.Add(line);
        }

        return lines.Count == 0 ? null : string.Join(' ', lines);
    }

    private static SkillDiagnostic ConvertDiagnostic(FrontmatterDiagnostic diagnostic) =>
        new(
            diagnostic.Id switch
            {
                "frontmatter.invalid_yaml" => "skill.invalid_yaml",
                "frontmatter.invalid_field" => "skill.invalid_frontmatter_field",
                _ => diagnostic.Id
            },
            new DirectLocaleKey(diagnostic.Message),
            NotificationType.Error);

    private static string? NullIfWhiteSpace(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*#\s+(?<title>.+?)\s*$")]
    private static partial Regex HeadingRegex();
}

internal sealed record SkillParseResult(
    string? FrontmatterName,
    string? FrontmatterDescription,
    string? HeadingName,
    string? FirstParagraph,
    string? DirectoryName,
    string MarkdownBody,
    IReadOnlyDictionary<string, string> Metadata,
    IReadOnlyList<SkillDiagnostic> Diagnostics
);
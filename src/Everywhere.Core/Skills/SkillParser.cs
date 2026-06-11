using Avalonia.Controls.Notifications;
using System.Text.RegularExpressions;
using Everywhere.Common;

namespace Everywhere.Skills;

internal static partial class SkillParser
{
    public static SkillParseResult Parse(string filePath, string folderName, string content)
    {
        var body = content;
        string? frontmatterName = null;
        string? frontmatterDescription = null;

        if (TryReadFrontmatter(content, out var frontmatter, out var remainingBody))
        {
            body = remainingBody;
            foreach (var line in frontmatter.Split('\n'))
            {
                var match = FrontmatterLineRegex().Match(line);
                if (!match.Success) continue;

                var key = match.Groups["key"].Value.Trim();
                var value = Unquote(match.Groups["value"].Value.Trim());
                if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
                {
                    frontmatterName = value;
                }
                else if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                {
                    frontmatterDescription = value;
                }
            }
        }

        frontmatterName = frontmatterName.NullIfWhiteSpace();
        frontmatterDescription = frontmatterDescription.NullIfWhiteSpace();
        var h1 = FindFirstHeading(body).NullIfWhiteSpace();
        var paragraph = FindFirstMeaningfulParagraph(body).NullIfWhiteSpace();
        var directoryName = folderName.NullIfWhiteSpace() ??
                            new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty).Name.NullIfWhiteSpace();
        var diagnostics = CreateDiagnostics(frontmatterName, frontmatterDescription, directoryName);

        return new SkillParseResult(
            frontmatterName,
            frontmatterDescription,
            h1,
            paragraph,
            directoryName,
            diagnostics);
    }

    private static IReadOnlyList<SkillDiagnostic> CreateDiagnostics(string? name, string? description, string? directoryName)
    {
        var diagnostics = new List<SkillDiagnostic>();
        if (string.IsNullOrWhiteSpace(name))
        {
            diagnostics.Add(new SkillDiagnostic(
                "skill.missing_name",
                new DirectResourceKey("SKILL.md is missing required frontmatter field 'name'."),
                NotificationType.Error));
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostics.Add(new SkillDiagnostic(
                "skill.missing_description",
                new DirectResourceKey("SKILL.md is missing required frontmatter field 'description'."),
                NotificationType.Error));
        }

        if (!string.IsNullOrWhiteSpace(name) &&
            !string.IsNullOrWhiteSpace(directoryName) &&
            !name.Equals(directoryName, StringComparison.Ordinal))
        {
            diagnostics.Add(new SkillDiagnostic(
                "skill.name_folder_mismatch",
                new DirectResourceKey("Skill name does not match the folder name; the folder name is used for the skill ID."),
                NotificationType.Warning));
        }

        return diagnostics;
    }

    private static bool TryReadFrontmatter(string content, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = content;

        if (!content.StartsWith("---", StringComparison.Ordinal)) return false;

        var firstLineEnd = content.IndexOf('\n');
        if (firstLineEnd < 0) return false;

        var searchStart = firstLineEnd + 1;
        var separatorIndex = content.IndexOf("\n---", searchStart, StringComparison.Ordinal);
        if (separatorIndex < 0) return false;

        var separatorEnd = separatorIndex + "\n---".Length;
        if (separatorEnd < content.Length && content[separatorEnd] == '\r') separatorEnd++;
        if (separatorEnd < content.Length && content[separatorEnd] == '\n') separatorEnd++;

        frontmatter = content[searchStart..separatorIndex].Trim('\r', '\n');
        body = content[Math.Min(separatorEnd, content.Length)..];
        return true;
    }

    private static string? FindFirstHeading(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var match = HeadingRegex().Match(line);
            if (match.Success) return match.Groups["title"].Value.Trim();
        }

        return null;
    }

    private static string? FindFirstMeaningfulParagraph(string body)
    {
        var lines = new List<string>();
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0) break;
                continue;
            }

            if (line.StartsWith('#') ||
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

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string? NullIfWhiteSpace(this string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"^\s*(?<key>[A-Za-z][A-Za-z0-9_-]*)\s*:\s*(?<value>.*)\s*$")]
    private static partial Regex FrontmatterLineRegex();

    [GeneratedRegex(@"^\s*#\s+(?<title>.+?)\s*$")]
    private static partial Regex HeadingRegex();
}

internal sealed record SkillParseResult(
    string? FrontmatterName,
    string? FrontmatterDescription,
    string? HeadingName,
    string? FirstParagraph,
    string? DirectoryName,
    IReadOnlyList<SkillDiagnostic> Diagnostics);


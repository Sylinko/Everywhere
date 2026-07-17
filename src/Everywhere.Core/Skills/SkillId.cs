using System.Text.RegularExpressions;

namespace Everywhere.Skills;

/// <summary>
/// Creates stable skill identifiers from source namespaces and directory names.
/// </summary>
internal static partial class SkillId
{
    /// <summary>
    /// Builds the canonical identifier used by prompts, resource URIs, and persisted settings.
    /// </summary>
    public static string FromFolder(SkillSourceRoot sourceRoot, string folderName) =>
        $"{SkillSource.GetSourceId(sourceRoot)}.{Normalize(folderName)}";

    /// <summary>
    /// Determines whether a value has the source-qualified shape required for a skill URI.
    /// </summary>
    public static bool IsFull(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.IndexOf('.');
        return separator > 0 &&
            separator < value.Length - 1 &&
            value.All(
                static character =>
                    character is >= 'a' and <= 'z' or
                    >= 'A' and <= 'Z' or
                    >= '0' and <= '9' or '.' or '_' or '-');
    }

    /// <summary>
    /// Normalizes a physical or embedded folder name without using display metadata.
    /// </summary>
    public static string Normalize(string value)
    {
        var normalized = IdInvalidCharacterRegex()
            .Replace(value.Trim().ToLowerInvariant(), "-")
            .Trim('-', '.', '_');
        return normalized.Length == 0 ? "skill" : normalized;
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex IdInvalidCharacterRegex();
}

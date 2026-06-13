using System.Text.RegularExpressions;
using ZLinq;

namespace Everywhere.Skills;

internal static partial class SkillReferenceResolver
{
    public static SkillResolutionResult Resolve(string reference, IEnumerable<SkillDescriptor> skills)
    {
        var normalizedReference = NormalizeReference(reference);
        if (string.IsNullOrWhiteSpace(normalizedReference))
        {
            return new SkillResolutionResult { Reference = reference };
        }

        var orderedSkills = skills
            .AsValueEnumerable()
            .OrderBy(skill => skill.SourceRoot)
            .ThenBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = FindCandidates(normalizedReference, orderedSkills);
        return new SkillResolutionResult
        {
            Reference = reference,
            Skill = candidates.FirstOrDefault(),
            Candidates = candidates
        };
    }

    private static List<SkillDescriptor> FindCandidates(string reference, IReadOnlyList<SkillDescriptor> skills)
    {
        if (TrySplitSourceQualifiedReference(reference, out var source, out var shortName))
        {
            return skills
                .AsValueEnumerable()
                .Where(skill => IsSourceMatch(skill, source) && IsShortNameMatch(skill, shortName))
                .ToList();
        }

        var normalizedReference = NormalizeIdFragment(reference);
        var exactMatches = skills
            .AsValueEnumerable()
            .Where(skill => skill.Id.Equals(normalizedReference, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (exactMatches.Count > 0) return exactMatches;

        return skills
            .AsValueEnumerable()
            .Where(skill => IsShortNameMatch(skill, normalizedReference))
            .ToList();
    }

    private static bool TrySplitSourceQualifiedReference(string reference, out string source, out string shortName)
    {
        source = string.Empty;
        shortName = string.Empty;

        var slashIndex = reference.IndexOf('/');
        if (slashIndex <= 0 || slashIndex == reference.Length - 1) return false;

        source = NormalizeIdFragment(reference[..slashIndex]);
        shortName = NormalizeIdFragment(reference[(slashIndex + 1)..]);
        return source.Length > 0 && shortName.Length > 0;
    }

    private static bool IsSourceMatch(SkillDescriptor skill, string source) =>
        SkillSource.GetSourceId(skill.SourceRoot).Equals(source, StringComparison.OrdinalIgnoreCase) ||
        skill.SourceName.Equals(source, StringComparison.OrdinalIgnoreCase) ||
        GetSourcePrefix(skill.Id).Equals(source, StringComparison.OrdinalIgnoreCase);

    private static bool IsShortNameMatch(SkillDescriptor skill, string shortName) =>
        GetShortId(skill.Id).Equals(shortName, StringComparison.OrdinalIgnoreCase) ||
        NormalizeIdFragment(skill.DirectoryName).Equals(shortName, StringComparison.OrdinalIgnoreCase);

    private static string GetSourcePrefix(string skillId)
    {
        var dotIndex = skillId.IndexOf('.');
        return dotIndex <= 0 ? string.Empty : skillId[..dotIndex];
    }

    private static string GetShortId(string skillId)
    {
        var dotIndex = skillId.IndexOf('.');
        return dotIndex < 0 || dotIndex == skillId.Length - 1 ? skillId : skillId[(dotIndex + 1)..];
    }

    private static string NormalizeReference(string reference)
    {
        var trimmed = reference.Trim();
        if (!trimmed.StartsWith("skill://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Trim('/');
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed["skill://".Length..].Trim('/');
        }

        var host = Uri.UnescapeDataString(uri.Host);
        var path = Uri.UnescapeDataString(uri.AbsolutePath).Trim('/');
        return string.IsNullOrWhiteSpace(path) ? host : $"{host}/{path}";
    }

    private static string NormalizeIdFragment(string value)
    {
        var normalized = IdInvalidCharacterRegex()
            .Replace(value.Trim().ToLowerInvariant(), "-")
            .Trim('-', '.', '_');
        return normalized.Length == 0 ? "skill" : normalized;
    }

    [GeneratedRegex(@"[^a-z0-9._-]+")]
    private static partial Regex IdInvalidCharacterRegex();
}

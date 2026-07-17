namespace Everywhere.Skills;

internal static class SkillReferenceResolver
{
    public static SkillResolutionResult Resolve(
        string reference,
        IReadOnlyDictionary<string, SkillDescriptor> skills)
    {
        var normalizedReference = NormalizeReference(reference);
        SkillDescriptor? skill = null;
        if (!string.IsNullOrWhiteSpace(normalizedReference) && SkillId.IsFull(normalizedReference))
        {
            skills.TryGetValue(normalizedReference, out skill);
        }

        return new SkillResolutionResult
        {
            Reference = reference,
            Skill = skill
        };
    }

    private static string NormalizeReference(string reference)
    {
        var trimmed = reference.Trim();
        if (!trimmed.StartsWith("skill://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed.Trim('/');
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("skill", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Length == 0 ||
            uri.Port != -1 ||
            uri.UserInfo.Length > 0 ||
            uri.Query.Length > 0 ||
            uri.Fragment.Length > 0 ||
            (uri.AbsolutePath.Length > 0 && uri.AbsolutePath != "/"))
        {
            return string.Empty;
        }

        return Uri.UnescapeDataString(uri.Host);
    }
}

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Interprets portable package-relative paths at archive and metadata boundaries.
/// </summary>
public static class PackagePath
{
    /// <summary>
    /// Rejects paths that could escape or name different files across supported operating systems.
    /// </summary>
    public static void ValidateRelative(string path)
    {
        var parts = path.Split('/');

        if (parts.Any(part =>
                part.Length == 0 ||
                part is "." or ".." ||
                part.EndsWith('.') ||
                part.EndsWith(' ') ||
                part.Any(c => char.IsControl(c) || "\\:<>\"|?*".Contains(c))))
        {
            throw new ModValidationException("InvalidPackagePath", $"Invalid package-relative path '{path}'.");
        }
    }

    /// <summary>
    /// Resolves a validated package path under an installation root.
    /// </summary>
    public static string Resolve(string root, string relativePath)
    {
        ValidateRelative(relativePath);
        return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }
}
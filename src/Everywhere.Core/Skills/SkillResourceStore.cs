using System.Collections.ObjectModel;
using System.Text;
using ZLinq;

namespace Everywhere.Skills;

/// <summary>
/// Resolves and enumerates resources for one skill without exposing its storage kind to the manager.
/// </summary>
public abstract class SkillResourceStore
{
    public abstract SkillResource Resolve(string skillId, string relativePath);

    public abstract IEnumerable<SkillResource> Enumerate(
        string skillId,
        string relativePath,
        bool recurseSubdirectories);

    protected static string BuildUri(string skillId, string relativePath) =>
        relativePath.Length == 0 ?
            $"skill://{skillId}/" :
            $"skill://{skillId}/{string.Join('/', relativePath.Split('/').Select(Uri.EscapeDataString))}";

    protected static bool IsPathInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (fullPath.Equals(fullDirectory, GetPathComparison())) return true;

        var prefix = fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, GetPathComparison());
    }

    private static StringComparison GetPathComparison()
    {
#if WINDOWS
        return StringComparison.OrdinalIgnoreCase;
#else
        return StringComparison.Ordinal;
#endif
    }
}

/// <summary>
/// Resolves resources from a physical skill directory.
/// </summary>
public sealed class LocalSkillResourceStore(string skillDirectory) : SkillResourceStore
{
    private readonly string _skillDirectory = Path.GetFullPath(skillDirectory);

    public override SkillResource Resolve(string skillId, string relativePath)
    {
        var physicalPath = GetPhysicalPath(relativePath);
        var uri = BuildUri(skillId, relativePath);
        if (!File.Exists(physicalPath) && !Directory.Exists(physicalPath))
        {
            throw new FileNotFoundException($"Skill resource '{uri}' was not found.", physicalPath);
        }

        return new SkillResource(uri, relativePath, Directory.Exists(physicalPath), physicalPath, default);
    }

    public override IEnumerable<SkillResource> Enumerate(
        string skillId,
        string relativePath,
        bool recurseSubdirectories)
    {
        var physicalRoot = GetPhysicalPath(relativePath);
        if (!Directory.Exists(physicalRoot)) yield break;

        var option = recurseSubdirectories ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        foreach (var path in Directory.EnumerateFileSystemEntries(physicalRoot, "*", option))
        {
            var relative = Path.GetRelativePath(_skillDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
            yield return Resolve(skillId, relative);
        }
    }

    private string GetPhysicalPath(string relativePath)
    {
        var physicalPath = relativePath.Length == 0 ?
            _skillDirectory :
            Path.GetFullPath(Path.Combine(_skillDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInsideDirectory(physicalPath, _skillDirectory))
        {
            throw new UnauthorizedAccessException("Skill resource path escapes the skill directory.");
        }

        return physicalPath;
    }
}

/// <summary>
/// Resolves resources supplied by a virtual skill provider.
/// </summary>
public sealed class VirtualSkillResourceStore : SkillResourceStore
{
    private readonly string _markdownContent;
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _resources;

    public VirtualSkillResourceStore(VirtualSkill skill)
    {
        _markdownContent = skill.MarkdownContent;
        _resources = new ReadOnlyDictionary<string, ReadOnlyMemory<byte>>(
            skill.Resources.AsValueEnumerable().ToDictionary(
                static item => NormalizePath(item.Key),
                static item => item.Value,
                StringComparer.OrdinalIgnoreCase));
    }

    public override SkillResource Resolve(string skillId, string relativePath)
    {
        var uri = BuildUri(skillId, relativePath);
        if (relativePath.Length == 0)
        {
            return new SkillResource(uri, relativePath, true, null, default);
        }

        if (relativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return new SkillResource(uri, relativePath, false, null, Encoding.UTF8.GetBytes(_markdownContent));
        }

        if (_resources.TryGetValue(relativePath, out var content))
        {
            return new SkillResource(uri, relativePath, false, null, content);
        }

        var directoryPrefix = relativePath.TrimEnd('/') + '/';
        if (_resources.Keys.AsValueEnumerable().Any(key => key.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            return new SkillResource(uri, relativePath, true, null, default);
        }

        throw new FileNotFoundException($"Skill resource '{uri}' was not found.");
    }

    public override IEnumerable<SkillResource> Enumerate(string skillId, string relativePath, bool recurseSubdirectories)
    {
        var prefix = relativePath.Length == 0 ? string.Empty : relativePath.TrimEnd('/') + '/';
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "SKILL.md" };
        foreach (var resourcePath in _resources.Keys)
        {
            paths.Add(resourcePath);
            var separator = resourcePath.IndexOf('/');
            while (separator >= 0)
            {
                paths.Add(resourcePath[..separator]);
                separator = resourcePath.IndexOf('/', separator + 1);
            }
        }

        foreach (var path in paths.Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var remainder = path[prefix.Length..];
            if (!recurseSubdirectories && remainder.Contains('/')) continue;
            yield return Resolve(skillId, path);
        }
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || Path.IsPathRooted(path))
        {
            throw new ArgumentException("Virtual skill resource paths must be relative and use '/' separators.", nameof(path));
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.AsValueEnumerable().Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("Virtual skill resource paths must stay inside the skill directory.", nameof(path));
        }

        return string.Join('/', segments);
    }
}
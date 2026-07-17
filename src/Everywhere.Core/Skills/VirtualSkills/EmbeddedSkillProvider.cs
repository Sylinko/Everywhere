using System.Text;

namespace Everywhere.Skills;

/// <summary>
/// Enumerates skills embedded in the Core assembly under the <c>Assets/Skills</c> resource tree.
/// </summary>
public sealed class EmbeddedSkillProvider : IVirtualSkillProvider
{
    private const string ResourcePrefix = "skills/";

    /// <inheritdoc />
    public async IAsyncEnumerable<VirtualSkill> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var assembly = typeof(EmbeddedSkillProvider).Assembly;
        var skills = new Dictionary<string, EmbeddedSkill>(StringComparer.OrdinalIgnoreCase);

        foreach (var resourceName in assembly.GetManifestResourceNames().Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resourceName.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relativeResourceName = resourceName[ResourcePrefix.Length..].Replace('\\', '/');
            var separator = relativeResourceName.IndexOf('/');
            if (separator <= 0 || separator == relativeResourceName.Length - 1) continue;

            var folderName = relativeResourceName[..separator];
            var relativePath = relativeResourceName[(separator + 1)..];
            if (!IsSafeRelativePath(relativePath)) continue;

            if (!skills.TryGetValue(folderName, out var skill))
            {
                skill = new EmbeddedSkill();
                skills.Add(folderName, skill);
            }

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null) continue;

            if (relativePath.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            {
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                skill.MarkdownContent = await reader.ReadToEndAsync(cancellationToken);
            }
            else
            {
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, cancellationToken);
                skill.Resources[relativePath] = buffer.ToArray();
            }
        }

        foreach (var (folderName, skill) in skills.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (skill.MarkdownContent is null) continue;

            var id = SkillId.FromFolder(SkillSourceRoot.BuiltIn, folderName);
            yield return new VirtualSkill(id, skill.MarkdownContent, skill.Resources);
            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Accumulates the manifest entries belonging to one embedded skill while a refresh is built.
    /// </summary>
    private sealed class EmbeddedSkill
    {
        public string? MarkdownContent { get; set; }

        public Dictionary<string, ReadOnlyMemory<byte>> Resources { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\\') || path.StartsWith('/')) return false;

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 && segments.All(segment => segment is not "." and not "..");
    }
}
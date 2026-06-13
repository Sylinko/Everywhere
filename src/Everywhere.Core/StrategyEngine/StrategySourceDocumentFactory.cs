using Everywhere.Common.Frontmatter;
using Everywhere.Skills;
using Avalonia.Controls.Notifications;

namespace Everywhere.StrategyEngine;

internal static class StrategySourceDocumentFactory
{
    public static async Task<StrategyDocument> LoadFileAsync(
        string filePath,
        string providerId,
        StrategyFromReferenceKind kind,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        var resolvedKind = ResolveKind(filePath, kind);
        return resolvedKind switch
        {
            StrategyFromReferenceKind.Strategy => StrategyDocumentParser.Parse(filePath, content, providerId),
            StrategyFromReferenceKind.Skill => CreateSkillDocument(filePath, content, providerId),
            _ => CreateMarkdownDocument(filePath, content, providerId)
        };
    }

    private static StrategyFromReferenceKind ResolveKind(string filePath, StrategyFromReferenceKind kind)
    {
        if (kind is not StrategyFromReferenceKind.Auto) return kind;

        if (filePath.EndsWith(".strategy.md", StringComparison.OrdinalIgnoreCase))
        {
            return StrategyFromReferenceKind.Strategy;
        }

        return Path.GetFileName(filePath).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)
            ? StrategyFromReferenceKind.Skill
            : StrategyFromReferenceKind.Markdown;
    }

    private static StrategyDocument CreateSkillDocument(string filePath, string content, string providerId)
    {
        var markdown = MarkdownFrontmatterParser.Parse(content);
        var directoryName = new DirectoryInfo(Path.GetDirectoryName(filePath) ?? string.Empty).Name;
        var skill = SkillParser.Parse(filePath, directoryName, content);
        var descriptor = new SkillDescriptor
        {
            Id = $"{providerId}.{directoryName}",
            Name = skill.FrontmatterName ?? skill.HeadingName ?? directoryName,
            Description = skill.FrontmatterDescription ?? skill.FirstParagraph,
            DirectoryName = directoryName,
            FilePath = Path.GetFullPath(filePath),
            MarkdownContent = content,
            MarkdownBody = skill.MarkdownBody,
            Metadata = skill.Metadata,
            SourceRoot = SkillSourceRoot.Everywhere,
            SourceName = providerId,
            SourceDirectoryPath = Path.GetDirectoryName(filePath) ?? string.Empty,
            IsValid = skill.Diagnostics.All(diagnostic => diagnostic.Type != NotificationType.Error),
            IsEnabled = true,
            Diagnostics = skill.Diagnostics
        };

        return CreateSkillDocument(descriptor, providerId, markdown.RawFrontmatter);
    }

    internal static StrategyDocument CreateSkillDocument(
        SkillDescriptor skill,
        string providerId,
        string? rawFrontmatter = null,
        IEnumerable<StrategyDiagnostic>? diagnostics = null)
    {
        var allDiagnostics = skill.Diagnostics
            .Select(diagnostic => new StrategyDiagnostic
            {
                Severity = StrategyDiagnosticSeverity.Warning,
                Code = diagnostic.Id,
                MessageKey = diagnostic.ContentKey,
                Path = skill.FilePath,
                ProviderId = providerId
            })
            .Concat(diagnostics ?? [])
            .ToList();

        var metadata = skill.Metadata.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
        metadata["skill.id"] = skill.Id;
        metadata["skill.sourceName"] = skill.SourceName;
        metadata["skill.sourceRoot"] = SkillSource.GetSourceId(skill.SourceRoot);

        return new StrategyDocument
        {
            Source = CreateSkillSource(skill),
            Schema = StrategyDefinitionV1.DefaultSchema,
            Definition = new StrategyDefinitionV1
            {
                Name = skill.Name,
                Description = skill.Description,
                Body = skill.MarkdownBody,
                Metadata = metadata
            },
            Body = skill.MarkdownBody,
            HasBodySection = true,
            RawFrontmatter = rawFrontmatter,
            Diagnostics = allDiagnostics
        };
    }

    private static StrategyDocument CreateMarkdownDocument(string filePath, string content, string providerId)
    {
        var body = MarkdownFrontmatterParser.NormalizeLineEndings(content);
        return new StrategyDocument
        {
            Source = CreateSource(filePath, providerId),
            Schema = StrategyDefinitionV1.DefaultSchema,
            Definition = new StrategyDefinitionV1
            {
                Name = Path.GetFileNameWithoutExtension(filePath),
                Body = body
            },
            Body = body,
            HasBodySection = true
        };
    }

    private static StrategySource CreateSource(string filePath, string providerId) => new()
    {
        ProviderId = providerId,
        Location = new Uri(Path.GetFullPath(filePath)),
        IsBuiltin = providerId.Equals("builtin", StringComparison.OrdinalIgnoreCase)
    };

    private static StrategySource CreateSkillSource(SkillDescriptor skill) => new()
    {
        ProviderId = "skill",
        Location = new Uri(Path.GetFullPath(skill.FilePath)),
        IsBuiltin = false
    };
}

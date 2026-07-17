using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Skills;

public sealed partial class SkillDescriptor : ObservableObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string FilePath { get; init; }

    public required string MarkdownContent { get; init; }

    public required string MarkdownBody { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? License => GetMetadataValue("license");

    public string? Compatibility => GetMetadataValue("compatibility");

    public string? Author => GetMetadataValue("metadata.author") ?? GetMetadataValue("author");

    public string? Version => GetMetadataValue("metadata.version") ?? GetMetadataValue("version");

    public required SkillSourceRoot SourceRoot { get; init; }

    public required string SourceName { get; init; }

    public required string SourceDirectoryPath { get; init; }

    public required bool IsValid { get; init; }

    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; init; } = [];

    public IDynamicLocaleKey? FirstDiagnosticContentKey => Diagnostics.FirstOrDefault()?.ContentKey;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    private string? GetMetadataValue(string key) =>
        Metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}

public sealed record SkillDiagnostic(string Id, IDynamicLocaleKey ContentKey, NotificationType Type);

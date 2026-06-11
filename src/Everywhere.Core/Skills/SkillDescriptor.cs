using Avalonia.Controls.Notifications;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Everywhere.Skills;

public sealed partial class SkillDescriptor : ObservableObject
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required string FilePath { get; init; }

    public required SkillSourceRoot SourceRoot { get; init; }

    public required string SourceName { get; init; }

    public required string SourceDirectoryPath { get; init; }

    public required bool IsValid { get; init; }

    public IReadOnlyList<SkillDiagnostic> Diagnostics { get; init; } = [];

    public IDynamicResourceKey? FirstDiagnosticContentKey => Diagnostics.FirstOrDefault()?.ContentKey;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    internal Action<SkillDescriptor, bool>? IsEnabledChangedHandler { get; init; }

    partial void OnIsEnabledChanged(bool value) => IsEnabledChangedHandler?.Invoke(this, value);
}

public sealed record SkillDiagnostic(string Id, IDynamicResourceKey ContentKey, NotificationType Type);

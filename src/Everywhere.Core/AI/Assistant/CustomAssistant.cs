using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using Lucide.Avalonia;

namespace Everywhere.AI;

/// <summary>
/// Allowing users to define and manage their own custom AI assistants.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class CustomAssistant : Assistant
{
    [SettingsItemIgnore]
    public Guid Id { get; set; } = Guid.CreateVersion7();

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ColoredIcon? Icon { get; set; } = new(ColoredIconType.Lucide) { Kind = LucideIconKind.Bot };

    [ObservableProperty]
    [SettingsItemIgnore]
    [MinLength(1)]
    [MaxLength(128)]
    public partial string? Name { get; set; }

    [SettingsItemIgnore]
    public string? Description
    {
        get;
        set => SetProperty(ref field, value?.SafeSubstring(0, 4096)?.Trim());
    }

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost"])]
    public SettingsControl<CustomAssistantInformationForm> InformationForm => new(
        new CustomAssistantInformationForm
        {
            CustomAssistant = this
        });

    /// <summary>
    /// Settings UI for selecting and previewing this assistant's Prompt Manager prompt.
    /// </summary>
    /// <remarks>
    /// This object is UI-only. The persisted value remains <see cref="SystemPromptId"/> so existing
    /// migration, runtime resolution, and settings JSON shape stay unchanged.
    /// </remarks>
    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.AssistantPromptSettings_Header, LocaleKey.AssistantPromptSettings_Description)]
    [SettingsItems(IsExpanded = false)]
    public AssistantPromptSettings PromptSettings => new(this);

    /// <summary>
    /// Prompt Manager prompt used as this assistant's active system prompt.
    /// </summary>
    /// <remarks>
    /// <see cref="Guid.Empty"/> means "use the built-in default prompt". Non-empty IDs point at
    /// rows in <c>prompt.db</c>; the prompt body is intentionally no longer
    /// stored inline with assistant settings.
    /// </remarks>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid SystemPromptId { get; set; } = Guid.Empty;
}

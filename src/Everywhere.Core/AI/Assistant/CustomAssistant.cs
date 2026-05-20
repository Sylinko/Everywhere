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
public sealed partial class CustomAssistant : Assistant, ISystemPromptProvider
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
    [DynamicResourceKey(LocaleKey.Empty)]
    [SettingsItem(Classes = ["Ghost"])]
    public SettingsControl<CustomAssistantInformationForm> InformationForm => new(
        new CustomAssistantInformationForm
        {
            CustomAssistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_SystemPrompt_Header,
        LocaleKey.CustomAssistant_SystemPrompt_Description)]
    [SettingsItem(Classes = ["Ghost"])]
    [SettingsStringItem(IsMultiline = true, MaxLength = 40960, Watermark = Prompts.DefaultSystemPrompt)]
    [DefaultValue(null)]
    public partial string? SystemPrompt { get; set; }
}
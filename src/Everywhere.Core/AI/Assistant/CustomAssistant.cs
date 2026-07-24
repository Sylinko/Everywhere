using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

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

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool IsToolCallEnabled { get; set; } = true;

    [JsonIgnore]
    [SettingsItemIgnore]
    public ToolCallStatus ToolCallStatus => SupportsToolCall switch
    {
        true when IsToolCallEnabled => ToolCallStatus.Enabled,
        true => ToolCallStatus.Disabled,
        _ => ToolCallStatus.NotSupported
    };

    /// <summary>
    /// Exact tool enablement overrides for this assistant. A null value means that the assistant follows global settings.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ObservableToolRulesets? ToolEnablementRulesets { get; set; }

    /// <summary>
    /// Settings UI for selecting and previewing this assistant's Prompt Manager prompt.
    /// </summary>
    /// <remarks>
    /// The control is UI-only. The selected prompt is persisted through <see cref="SystemPromptId"/>.
    /// </remarks>
    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.CustomAssistant_PromptSelector_Header)]
    [SettingsItem(Index = 1)]
    public SettingsControl<CustomAssistantPromptSelector> PromptSelector => new(x =>
        new CustomAssistantPromptSelector(this, x)
        {
            [!CustomAssistantPromptSelector.SelectedIdProperty] = CompiledBinding.Create(
                (CustomAssistant xx) => xx.SystemPromptId,
                source: this,
                mode: BindingMode.TwoWay)
        });

    /// <summary>
    /// Settings UI for this assistant's tool availability.
    /// </summary>
    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.ChatPluginPage_Title)]
    [SettingsItem(Index = 2)]
    public SettingsControl<CustomAssistantToolSettingsView> ToolSettings => new(x =>
        new CustomAssistantToolSettingsView(
            x.GetRequiredService<IChatPluginManager>(),
            x.GetRequiredService<Settings>())
        {
            Assistant = this
        });
}

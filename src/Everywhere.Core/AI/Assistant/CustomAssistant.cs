using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;
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
    [NotifyPropertyChangedFor(nameof(ToolCallStatus))]
    public partial bool IsToolCallEnabled { get; set; } = true;

    [JsonIgnore]
    [SettingsItemIgnore]
    public ToolCallStatus ToolCallStatus => Configuration.SupportsToolCall switch
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
        new CustomAssistantToolSettingsView(x.GetRequiredService<IChatPluginManager>(), x.GetRequiredService<Settings>())
        {
            Assistant = this
        });

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_VisualContextLengthLimit_Header,
        LocaleKey.CustomAssistant_VisualContextLengthLimit_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = 1)]
    public partial VisualContextLengthLimit VisualContextLengthLimit { get; set; } = VisualContextLengthLimit.Balanced;

    /// <summary>
    /// Gets or sets the percentage of the declared context limit at which automatic context
    /// compression starts.
    /// </summary>
    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ContextCompressionThreshold_Header,
        LocaleKey.CustomAssistant_ContextCompressionThreshold_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = 2)]
    [SettingsIntegerItem(Min = 5, Max = 95)]
    [Range(5, 95)]
    [DefaultValue(80)]
    public partial int ContextCompressionThreshold { get; set; } = 80;

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_MaxContextRounds_Header,
        LocaleKey.CustomAssistant_MaxContextRounds_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = 3)]
    [SettingsIntegerItem(Min = -1, Max = 30)]
    [Range(-1, 30)]
    [DefaultValue(-1)]
    public partial int MaxContextRounds { get; set; } = -1;

    protected override void OnConfigurationPropertyChanged(string? propertyName)
    {
        if (propertyName is nameof(AssistantConfiguration.SupportsToolCall))
        {
            OnPropertyChanged(nameof(ToolCallStatus));
        }
    }
}
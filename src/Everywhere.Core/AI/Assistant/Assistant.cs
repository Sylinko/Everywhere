using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI.Configurator;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI;

public abstract partial class Assistant : ObservableValidator, IModelDefinition
{
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? Endpoint { get; set; }

    /// <summary>
    /// The GUID of the API key to use for this custom assistant.
    /// </summary>
    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid ApiKey { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool SupportsReasoning { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool SupportsToolCall { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial bool SupportsTemperature { get; set; } = true;

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Modalities InputModalities { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Modalities OutputModalities { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial int ContextLimit { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial int OutputLimit { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ModelSpecializations Specializations { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial DateOnly? DeprecationDate { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyPropertyChangedFor(nameof(Configurator))]
    public partial AssistantConfiguratorType ConfiguratorType { get; set; } = AssistantConfiguratorType.Official;

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? ModelProviderTemplateId { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? ModelDefinitionTemplateId { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    public AssistantConfigurator Configurator => GetConfigurator(ConfiguratorType);

    [JsonIgnore]
    [DynamicResourceKey(LocaleKey.Assistant_ConfiguratorSelector_Header)]
    protected SettingsControl<AssistantConfiguratorSelector> ConfiguratorSelector => new(
        new AssistantConfiguratorSelector
        {
            Assistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_RequestTimeoutSeconds_Header,
        LocaleKey.Assistant_RequestTimeoutSeconds_Description)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    [DefaultValue(20)]
    public partial int RequestTimeoutSeconds { get; set; } = 20;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsTemperature))]
    [SettingsDoubleItem(Min = 0.0, Max = 2.0, Step = 0.01)]
    public partial Customizable<double> Temperature { get; set; } = 1.0;

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsTemperature))]
    [SettingsDoubleItem(Min = 0.0, Max = 1.0, Step = 0.01)]
    public partial Customizable<double> TopP { get; set; } = 0.9;

    [JsonIgnore]
    [SettingsItemIgnore]
#pragma warning disable CA1822 // Required non-static for binding, TODO: make source generator more robust, use ActualType.StaticProperty instead of Path
    public string?[] ReasoningEnabledOptions { get; } = [null, "adaptive", "enabled", "disabled"];
#pragma warning restore CA1822

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_ThinkingType_Header,
        LocaleKey.Assistant_ThinkingType_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsReasoning))]
    [SettingsSelectionItem(nameof(ReasoningEnabledOptions))]
    public partial string? ThinkingType { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
#pragma warning disable CA1822 // Required non-static for binding
    public string?[] DefaultReasoningEffortOptions { get; } = [null, "auto", "minimal", "low", "medium", "high", "xhigh", "max"];
#pragma warning restore CA1822

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_ReasoningEffort_Header,
        LocaleKey.Assistant_ReasoningEffort_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsReasoning))]
    [SettingsSelectionItem(nameof(DefaultReasoningEffortOptions), IsEditable = true)]
    public partial string? ReasoningEffort { get; set; }

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_ThinkingBudget_Header,
        LocaleKey.Assistant_ThinkingBudget_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsReasoning))]
    public partial string? ThinkingBudget { get; set; }

    private readonly OfficialAssistantConfigurator _officialConfigurator;
    private readonly PresetBasedAssistantConfigurator _presetBasedConfigurator;
    private readonly AdvancedAssistantConfigurator _advancedConfigurator;

    protected Assistant()
    {
        _officialConfigurator = new OfficialAssistantConfigurator(this);
        _presetBasedConfigurator = new PresetBasedAssistantConfigurator(this);
        _advancedConfigurator = new AdvancedAssistantConfigurator(this);
    }

    public AssistantConfigurator GetConfigurator(AssistantConfiguratorType type) => type switch
    {
        AssistantConfiguratorType.Official => _officialConfigurator,
        AssistantConfiguratorType.PresetBased => _presetBasedConfigurator,
        _ => _advancedConfigurator
    };

    public void ApplyTemplate(ModelProviderTemplate? modelProviderTemplate)
    {
        if (modelProviderTemplate is not null)
        {
            Endpoint = modelProviderTemplate.Endpoint;
            Schema = modelProviderTemplate.Schema;
            RequestTimeoutSeconds = modelProviderTemplate.RequestTimeoutSeconds;
        }
        else
        {
            Endpoint = string.Empty;
            Schema = ModelProviderSchema.OpenAI;
            RequestTimeoutSeconds = 20;
        }
    }

    public void ApplyTemplate(ModelDefinitionTemplate? modelDefinitionTemplate)
    {
        if (modelDefinitionTemplate is not null)
        {
            ModelId = modelDefinitionTemplate.ModelId;
            SupportsReasoning = modelDefinitionTemplate.SupportsReasoning;
            SupportsToolCall = modelDefinitionTemplate.SupportsToolCall;
            SupportsTemperature = modelDefinitionTemplate.SupportsTemperature;
            InputModalities = modelDefinitionTemplate.InputModalities;
            OutputModalities = modelDefinitionTemplate.OutputModalities;
            ContextLimit = modelDefinitionTemplate.ContextLimit;
            OutputLimit = modelDefinitionTemplate.OutputLimit;
            Specializations = modelDefinitionTemplate.Specializations;
            DeprecationDate = modelDefinitionTemplate.DeprecationDate;
        }
        else
        {
            ModelId = string.Empty;
            SupportsReasoning = false;
            SupportsToolCall = false;
            SupportsTemperature = true;
            InputModalities = default;
            OutputModalities = default;
            ContextLimit = 0;
            OutputLimit = 0;
            Specializations = default;
            DeprecationDate = null;
        }
    }
}

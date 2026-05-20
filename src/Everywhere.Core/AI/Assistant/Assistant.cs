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
    [NotifyPropertyChangedFor(nameof(IsOpenAI), nameof(IsOpenAIResponses), nameof(IsGoogle), nameof(IsAnthropic))]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? ModelId { get; set; }

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
    [SettingsItem(Classes = ["Ghost"])]
    protected SettingsControl<AssistantConfiguratorSelector> ConfiguratorSelector => new(
        new AssistantConfiguratorSelector
        {
            Assistant = this
        });

    [ObservableProperty]
    [DynamicResourceKey(
        LocaleKey.Assistant_RequestTimeoutSeconds_Header,
        LocaleKey.Assistant_RequestTimeoutSeconds_Description)]
    [SettingsItem(Group = "Advanced")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    [DefaultValue(20)]
    public partial int RequestTimeoutSeconds { get; set; } = 20;

    [DynamicResourceKey(
        LocaleKey.Assistant_Temperature_Header,
        LocaleKey.Assistant_Temperature_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsTemperature), Group = "Advanced")]
    [SettingsDoubleItem(Min = 0.0, Max = 2.0, Step = 0.01)]
    public Customizable<double> Temperature { get; } = new(1.0);

    [DynamicResourceKey(
        LocaleKey.Assistant_TopP_Header,
        LocaleKey.Assistant_TopP_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(SupportsTemperature), Group = "Advanced")]
    [SettingsDoubleItem(Min = 0.0, Max = 1.0, Step = 0.01)]
    public Customizable<double> TopP { get; } = new(0.9);

    public bool IsOpenAI => Schema == ModelProviderSchema.OpenAI;

    [DynamicResourceKey(
        LocaleKey.Assistant_OpenAIOptions_Header,
        LocaleKey.Assistant_OpenAIOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsOpenAI), Group = "Advanced")]
    [SettingsItems]
    public OpenAIOptions OpenAIOptions { get; } = new();

    public bool IsOpenAIResponses => Schema == ModelProviderSchema.OpenAIResponses;

    [DynamicResourceKey(
        LocaleKey.Assistant_OpenAIResponsesOptions_Header,
        LocaleKey.Assistant_OpenAIResponsesOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsOpenAIResponses), Group = "Advanced")]
    [SettingsItems]
    public OpenAIResponsesOptions OpenAIResponsesOptions { get; } = new();

    public bool IsAnthropic => Schema == ModelProviderSchema.Anthropic;

    [DynamicResourceKey(
        LocaleKey.Assistant_AnthropicOptions_Header,
        LocaleKey.Assistant_AnthropicOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsAnthropic), Group = "Advanced")]
    [SettingsItems]
    public AnthropicOptions AnthropicOptions { get; } = new();

    public bool IsGoogle => Schema == ModelProviderSchema.Google;

    [DynamicResourceKey(
        LocaleKey.Assistant_GoogleOptions_Header,
        LocaleKey.Assistant_GoogleOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsGoogle), Group = "Advanced")]
    [SettingsItems]
    public GoogleOptions GoogleOptions { get; } = new();

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
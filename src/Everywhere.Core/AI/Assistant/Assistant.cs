using System.ComponentModel;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI.Configurator;
using Everywhere.Configuration;
using Everywhere.Views;

namespace Everywhere.AI;

public abstract partial class Assistant : ObservableValidator
{
    [SettingsItemIgnore]
    public AssistantConfiguration Configuration
    {
        get => _configuration;
        set
        {
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            value ??= new OfficialAssistantConfiguration();
            if (ReferenceEquals(_configuration, value)) return;

            var previous = _configuration;
            previous.PropertyChanged -= HandleConfigurationPropertyChanged;
            _configuration = value;
            value.PropertyChanged += HandleConfigurationPropertyChanged;
            OnPropertyChanged();

            if (previous.GetType() != value.GetType())
            {
                OnPropertyChanged(nameof(ConfiguratorType));
                OnPropertyChanged(nameof(Configurator));
            }
            if (previous.Schema != value.Schema) NotifySchemaChanged();

            _officialConfigurator.NotifyConfigurationChanged(previous, value);
            _presetBasedConfigurator.NotifyConfigurationChanged(previous, value);
            _advancedConfigurator.NotifyConfigurationChanged(previous, value);
            OnConfigurationPropertyChanged(null);
        }
    }

    [JsonIgnore]
    [SettingsItemIgnore]
    public AssistantConfiguratorType ConfiguratorType
    {
        get => Configuration switch
        {
            OfficialAssistantConfiguration => AssistantConfiguratorType.Official,
            PresetAssistantConfiguration => AssistantConfiguratorType.PresetBased,
            _ => AssistantConfiguratorType.Advanced
        };
        set => SwitchConfiguration(value);
    }

    [JsonIgnore]
    [SettingsItemIgnore]
    public AssistantConfigurator Configurator => ConfiguratorType switch
    {
        AssistantConfiguratorType.Official => _officialConfigurator,
        AssistantConfiguratorType.PresetBased => _presetBasedConfigurator,
        _ => _advancedConfigurator
    };

    [JsonIgnore]
    [DynamicLocaleKey(LocaleKey.Assistant_ConfiguratorSelector_Header)]
    [SettingsItem(Classes = ["Ghost"], Index = 0)]
    protected SettingsControl<AssistantConfiguratorSelector> ConfiguratorSelector => new(
        new AssistantConfiguratorSelector
        {
            Assistant = this
        });

    [ObservableProperty]
    [DynamicLocaleKey(
        LocaleKey.Assistant_RequestTimeoutSeconds_Header,
        LocaleKey.Assistant_RequestTimeoutSeconds_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = 0)]
    [SettingsIntegerItem(IsSliderVisible = false)]
    [DefaultValue(20)]
    public partial int RequestTimeoutSeconds { get; set; } = 20;

    [JsonIgnore]
    public bool IsOpenAI => Configuration.Schema == ModelProviderSchema.OpenAI;

    [DynamicLocaleKey(
        LocaleKey.Assistant_OpenAIOptions_Header,
        LocaleKey.Assistant_OpenAIOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsOpenAI), Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue)]
    [SettingsItems(IsExpanded = false)]
    public OpenAIOptions OpenAIOptions { get; } = new();

    [JsonIgnore]
    public bool IsOpenAIResponses => Configuration.Schema == ModelProviderSchema.OpenAIResponses;

    [DynamicLocaleKey(
        LocaleKey.Assistant_OpenAIResponsesOptions_Header,
        LocaleKey.Assistant_OpenAIResponsesOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsOpenAIResponses), Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue)]
    [SettingsItems(IsExpanded = false)]
    public OpenAIResponsesOptions OpenAIResponsesOptions { get; } = new();

    [JsonIgnore]
    public bool IsAnthropic => Configuration.Schema == ModelProviderSchema.Anthropic;

    [DynamicLocaleKey(
        LocaleKey.Assistant_AnthropicOptions_Header,
        LocaleKey.Assistant_AnthropicOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsAnthropic), Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue)]
    [SettingsItems(IsExpanded = false)]
    public AnthropicOptions AnthropicOptions { get; } = new();

    [JsonIgnore]
    public bool IsGoogle => Configuration.Schema == ModelProviderSchema.Google;

    [DynamicLocaleKey(
        LocaleKey.Assistant_GoogleOptions_Header,
        LocaleKey.Assistant_GoogleOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsGoogle), Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue)]
    [SettingsItems(IsExpanded = false)]
    public GoogleOptions GoogleOptions { get; } = new();

    [JsonIgnore]
    public bool IsMistral => Configuration.Schema == ModelProviderSchema.Mistral;

    [DynamicLocaleKey(
        LocaleKey.Assistant_MistralOptions_Header,
        LocaleKey.Assistant_MistralOptions_Description)]
    [SettingsItem(IsVisibleBindingPath = nameof(IsMistral), Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue)]
    [SettingsItems(IsExpanded = false)]
    public MistralOptions MistralOptions { get; } = new();

    private readonly Dictionary<AssistantConfiguratorType, AssistantConfiguration> _modeConfigurations = [];
    private readonly OfficialAssistantConfigurator _officialConfigurator;
    private readonly PresetBasedAssistantConfigurator _presetBasedConfigurator;
    private readonly AdvancedAssistantConfigurator _advancedConfigurator;
    private AssistantConfiguration _configuration = new OfficialAssistantConfiguration();

    protected Assistant()
    {
        _configuration.PropertyChanged += HandleConfigurationPropertyChanged;
        _officialConfigurator = new OfficialAssistantConfigurator(this);
        _presetBasedConfigurator = new PresetBasedAssistantConfigurator(this);
        _advancedConfigurator = new AdvancedAssistantConfigurator(this);
    }

    protected virtual void OnConfigurationPropertyChanged(string? propertyName) { }

    private void HandleConfigurationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantConfiguration.Schema)) NotifySchemaChanged();
        _officialConfigurator.NotifyConfigurationChanged(e.PropertyName);
        _presetBasedConfigurator.NotifyConfigurationChanged(e.PropertyName);
        _advancedConfigurator.NotifyConfigurationChanged(e.PropertyName);
        OnConfigurationPropertyChanged(e.PropertyName);
    }

    private void NotifySchemaChanged()
    {
        OnPropertyChanged(nameof(IsOpenAI));
        OnPropertyChanged(nameof(IsOpenAIResponses));
        OnPropertyChanged(nameof(IsAnthropic));
        OnPropertyChanged(nameof(IsGoogle));
        OnPropertyChanged(nameof(IsMistral));
    }

    /// <summary>
    /// Mode switching retains typed drafts for this assistant's editing lifetime.
    /// </summary>
    private void SwitchConfiguration(AssistantConfiguratorType type)
    {
        if (type == ConfiguratorType) return;
        _modeConfigurations[ConfiguratorType] = Configuration;
        if (_modeConfigurations.TryGetValue(type, out var previous))
        {
            Configuration = previous;
            return;
        }

        Configuration = type switch
        {
            AssistantConfiguratorType.Official => AssistantSnapshotMapper.ToOfficial(Configuration),
            AssistantConfiguratorType.PresetBased => AssistantSnapshotMapper.ToPreset(Configuration),
            _ => AssistantSnapshotMapper.ToAdvanced(Configuration)
        };
    }

    public void ApplyTemplate(ModelProviderTemplate? template)
    {
        if (template is null) return;
        Configuration.Endpoint = template.Endpoint;
        Configuration.Schema = template.Schema;
        RequestTimeoutSeconds = template.RequestTimeoutSeconds;
    }

    public void ApplyTemplate(ModelDefinitionTemplate? template)
    {
        if (template is not null) Configuration.Apply(template);
    }
}
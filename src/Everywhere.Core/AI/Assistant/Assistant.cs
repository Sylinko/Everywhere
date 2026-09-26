using System.ComponentModel;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using CommunityToolkit.Mvvm.ComponentModel;
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

            _configuration.PropertyChanged -= HandleConfigurationPropertyChanged;
            _configuration = value;
            value.PropertyChanged += HandleConfigurationPropertyChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveSchemaOptions));
            OnPropertyChanged(nameof(EffectiveReasoningOptions));
            EffectiveReasoningOptions?.DefaultReasoningEffortValues = null;
            OnConfigurationPropertyChanged(null);
        }
    }

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

    [DynamicLocaleKey(
        LocaleKey.Assistant_OpenAIOptions_Header,
        LocaleKey.Assistant_OpenAIOptions_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue, Modifier = nameof(BindSchemaOptionsVisibility))]
    [SettingsItems(IsExpanded = false)]
    public OpenAIOptions OpenAIOptions { get; } = new();

    [DynamicLocaleKey(
        LocaleKey.Assistant_OpenAIResponsesOptions_Header,
        LocaleKey.Assistant_OpenAIResponsesOptions_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue, Modifier = nameof(BindSchemaOptionsVisibility))]
    [SettingsItems(IsExpanded = false)]
    public OpenAIResponsesOptions OpenAIResponsesOptions { get; } = new();

    [DynamicLocaleKey(
        LocaleKey.Assistant_AnthropicOptions_Header,
        LocaleKey.Assistant_AnthropicOptions_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue, Modifier = nameof(BindSchemaOptionsVisibility))]
    [SettingsItems(IsExpanded = false)]
    public AnthropicOptions AnthropicOptions { get; } = new();

    [DynamicLocaleKey(
        LocaleKey.Assistant_GoogleOptions_Header,
        LocaleKey.Assistant_GoogleOptions_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue, Modifier = nameof(BindSchemaOptionsVisibility))]
    [SettingsItems(IsExpanded = false)]
    public GoogleOptions GoogleOptions { get; } = new();

    [DynamicLocaleKey(
        LocaleKey.Assistant_MistralOptions_Header,
        LocaleKey.Assistant_MistralOptions_Description)]
    [SettingsItem(Group = LocaleKey.Assistant_AdvancedSettings, Index = int.MaxValue, Modifier = nameof(BindSchemaOptionsVisibility))]
    [SettingsItems(IsExpanded = false)]
    public MistralOptions MistralOptions { get; } = new();

    protected SettingsTemplatedItem BindSchemaOptionsVisibility(SettingsTemplatedItem item)
    {
        item[!SettingsItem.IsVisibleProperty] = CompiledBinding.Create(
            (Assistant x) => x.Configuration.Schema,
            source: this,
            converter: ObjectConverters.Equal,
            converterParameter: item.Value.As<ModelSchemaOptions>()?.Schema);
        return item;
    }

    [JsonIgnore]
    [SettingsItemIgnore]
    public ModelSchemaOptions? EffectiveSchemaOptions => Configuration.Schema switch
    {
        ModelProviderSchema.OpenAI => OpenAIOptions,
        ModelProviderSchema.OpenAIResponses => OpenAIResponsesOptions,
        ModelProviderSchema.Anthropic => AnthropicOptions,
        ModelProviderSchema.Google => GoogleOptions,
        ModelProviderSchema.Mistral => MistralOptions,
        _ => null
    };

    [JsonIgnore]
    [SettingsItemIgnore]
    public ReasoningModelSchemaOptions? EffectiveReasoningOptions =>
        EffectiveSchemaOptions as ReasoningModelSchemaOptions;

    private AssistantConfiguration _configuration = new OfficialAssistantConfiguration();

    protected Assistant()
    {
        _configuration.PropertyChanged += HandleConfigurationPropertyChanged;
    }

    protected virtual void OnConfigurationPropertyChanged(string? propertyName) { }

    private void HandleConfigurationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantConfiguration.Schema))
        {
            OnPropertyChanged(nameof(EffectiveSchemaOptions));
            OnPropertyChanged(nameof(EffectiveReasoningOptions));
            EffectiveReasoningOptions?.DefaultReasoningEffortValues = null;
        }

        OnConfigurationPropertyChanged(e.PropertyName);
    }
}
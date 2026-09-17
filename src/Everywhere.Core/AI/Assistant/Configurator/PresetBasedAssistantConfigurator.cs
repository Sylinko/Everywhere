using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for preset-based model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class PresetBasedAssistantConfigurator(Assistant owner) : AssistantConfigurator
{
    [JsonIgnore]
    [SettingsItemIgnore]
    public static IReadOnlyList<ModelProviderTemplate> ModelProviderTemplates => PresetModelTemplates.Providers;

    private static IPresetModelProvider Catalog => ServiceLocator.Resolve<IPresetModelProvider>();

    /// <summary>
    /// The ID of the model provider to use for this custom assistant.
    /// This ID should correspond to one of the available model providers in the application.
    /// </summary>
    [SettingsItemIgnore]
    public string? ModelProviderTemplateId
    {
        get => (owner.Configuration as PresetAssistantConfiguration)?.ProviderId;
        set
        {
            if (value == ModelProviderTemplateId) return;

            var provider = PresetModelTemplates.Providers.AsValueEnumerable().FirstOrDefault(p => p.Id == value);
            if (provider is null) return;

            var models = Catalog.GetModelDefinitions(value);
            var model = models.FirstOrDefault(m => m.IsDefault) ?? models.FirstOrDefault();
            var configuration = new PresetAssistantConfiguration
            {
                ProviderId = value,
                Endpoint = provider.Endpoint,
                Schema = provider.Schema,
                ApiKey = owner.Configuration.ApiKey
            };

            if (model is not null) configuration.Apply(model);
            owner.RequestTimeoutSeconds = provider.RequestTimeoutSeconds;
            owner.Configuration = configuration;
        }
    }

    [Required]
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsItem(Group = "_", Classes = ["PresetModelProvider"])]
    [SettingsSelectionItem(nameof(ModelProviderTemplates), DataTemplateKey = typeof(ModelProviderTemplate))]
    public ModelProviderTemplate? ModelProviderTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId);
        set => ModelProviderTemplateId = value?.Id;
    }

    [SettingsItemIgnore]
    public Guid ApiKey
    {
        get => owner.Configuration.ApiKey;
        set
        {
            if (owner.Configuration.ApiKey == value) return;

            owner.Configuration.ApiKey = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.Assistant_ApiKey_Header,
        LocaleKey.Assistant_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        serviceProvider => new ApiKeyComboBox(serviceProvider.GetRequiredService<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (PresetBasedAssistantConfigurator x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay),
            [!ApiKeyComboBox.DefaultNameProperty] = new Binding("ModelProviderTemplate.DisplayNameKey^")
            {
                Source = this,
                Mode = BindingMode.OneWay,
                TargetNullValue = string.Empty,
                FallbackValue = string.Empty
            }
        });

    [Required]
    [JsonIgnore]
    [SettingsItemIgnore]
    public ModelDefinitionTemplate? ModelDefinitionTemplate
    {
        get => string.IsNullOrWhiteSpace(owner.Configuration.ModelId) ? null : owner.Configuration.ToTemplate();
        set
        {
            if (value is not null) owner.ApplyTemplate(value);
        }
    }

    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<PresetModelDefinitionSelector> ModelDefinitionSelector =>
        new(sp => new PresetModelDefinitionSelector(sp.GetRequiredService<IPresetModelProvider>(), this), false);

    internal override void NotifyConfigurationChanged(AssistantConfiguration previous, AssistantConfiguration current)
    {
        // TODO: this is shit
        if ((previous as PresetAssistantConfiguration)?.ProviderId != (current as PresetAssistantConfiguration)?.ProviderId)
        {
            OnPropertyChanged(nameof(ModelProviderTemplateId));
            OnPropertyChanged(nameof(ModelProviderTemplate));
        }
        if (previous.ApiKey != current.ApiKey) OnPropertyChanged(nameof(ApiKey));
        if (previous.ModelId != current.ModelId || previous.ContextLimit != current.ContextLimit ||
            previous.OutputLimit != current.OutputLimit || previous.SupportsToolCall != current.SupportsToolCall ||
            previous.Name != current.Name || previous.InputModalities != current.InputModalities ||
            previous.OutputModalities != current.OutputModalities)
            OnPropertyChanged(nameof(ModelDefinitionTemplate));
    }

    internal override void NotifyConfigurationChanged(string? propertyName)
    {
        // TODO: this is shit
        if (propertyName == nameof(PresetAssistantConfiguration.ProviderId))
        {
            OnPropertyChanged(nameof(ModelProviderTemplateId));
            OnPropertyChanged(nameof(ModelProviderTemplate));
        }
        else if (propertyName == nameof(AssistantConfiguration.ApiKey)) OnPropertyChanged(nameof(ApiKey));
        else if (propertyName is nameof(AssistantConfiguration.ModelId) or nameof(AssistantConfiguration.Name) or
                 nameof(AssistantConfiguration.SupportsToolCall) or nameof(AssistantConfiguration.InputModalities) or
                 nameof(AssistantConfiguration.OutputModalities) or nameof(AssistantConfiguration.ContextLimit) or
                 nameof(AssistantConfiguration.OutputLimit) or nameof(AssistantConfiguration.Specializations) or
                 nameof(AssistantConfiguration.DeprecationDate) or nameof(AssistantConfiguration.ReleaseDate) or
                 nameof(AssistantConfiguration.KnowledgeCutoff))
            OnPropertyChanged(nameof(ModelDefinitionTemplate));
    }

    public override Assistant ResolveAssistant(ModelSpecializations specialization)
    {
        if (specialization == ModelSpecializations.Default || owner.Configuration.Specializations.HasFlag(specialization))
        {
            // If the current assistant already has the specialization, return it directly.
            return owner;
        }

        if (ModelProviderTemplate is { } modelProviderTemplate &&
            Catalog.GetModelDefinitions(modelProviderTemplate.Id).FirstOrDefault(m => m.Specializations.HasFlag(specialization)) is { } modelDefinitionTemplate)
        {
            var systemAssistant = new SystemAssistant(specialization)
            {
                Configuration = new PresetAssistantConfiguration
                {
                    ProviderId = modelProviderTemplate.Id,
                    ApiKey = owner.Configuration.ApiKey
                }
            };
            systemAssistant.ApplyTemplate(modelProviderTemplate);
            systemAssistant.ApplyTemplate(modelDefinitionTemplate);
            return systemAssistant;
        }

        // Not found, fallback to selected owner
        return owner;
    }
}

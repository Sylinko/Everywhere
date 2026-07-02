using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for preset-based model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class PresetBasedAssistantConfigurator(Assistant owner) : AssistantConfigurator
{
    /// <summary>
    /// The ID of the model provider to use for this custom assistant.
    /// This ID should correspond to one of the available model providers in the application.
    /// </summary>
    [SettingsItemIgnore]
    public string? ModelProviderTemplateId
    {
        get => owner.ModelProviderTemplateId;
        set
        {
            if (value == owner.ModelProviderTemplateId) return;
            owner.ModelProviderTemplateId = value;

            owner.ApplyTemplate(ModelProviderTemplate);
            ModelDefinitionTemplate = ModelDefinitionTemplates.AsValueEnumerable().FirstOrDefault(m => m.IsDefault);

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelProviderTemplate));
            OnPropertyChanged(nameof(ModelDefinitionTemplates));
        }
    }

    [Required]
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsItem(Group = "_")]
    [SettingsSelectionItem(nameof(ModelProviderTemplates), DataTemplateKey = typeof(ModelProviderTemplate))]
    public ModelProviderTemplate? ModelProviderTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId);
        set => ModelProviderTemplateId = value?.Id;
    }

    [SettingsItemIgnore]
    public Guid ApiKey
    {
        get => owner.ApiKey;
        set
        {
            if (owner.ApiKey == value) return;

            owner.ApiKey = value;
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
            [!ApiKeyComboBox.DefaultNameProperty] = CompiledBinding.Create(
                (PresetBasedAssistantConfigurator x) => x.ModelProviderTemplate!.DisplayName,
                source: this,
                targetNullValue: string.Empty,
                fallbackValue: string.Empty)
        });

    [JsonIgnore]
    [SettingsItemIgnore]
    private IEnumerable<ModelDefinitionTemplate> ModelDefinitionTemplates => ModelProviderTemplate?.ModelDefinitions ?? [];

    [SettingsItemIgnore]
    public string? ModelDefinitionTemplateId
    {
        get => owner.ModelDefinitionTemplateId;
        set
        {
            if (value == owner.ModelDefinitionTemplateId) return;
            owner.ModelDefinitionTemplateId = value;

            owner.ApplyTemplate(ModelDefinitionTemplate);

            OnPropertyChanged();
            OnPropertyChanged(nameof(ModelDefinitionTemplate));
        }
    }

    [Required]
    [JsonIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
    [SettingsItem(Group = "_")]
    [SettingsSelectionItem(nameof(ModelDefinitionTemplates), DataTemplateKey = typeof(ModelDefinitionTemplate))]
    public ModelDefinitionTemplate? ModelDefinitionTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId)?
            .ModelDefinitions.FirstOrDefault(m => m.ModelId == ModelDefinitionTemplateId);
        set => ModelDefinitionTemplateId = value?.ModelId;
    }

    public override void Backup()
    {
        Backup(owner.ApiKey);
        Backup(owner.ModelProviderTemplateId);
        Backup(owner.ModelDefinitionTemplateId);
    }

    public override void Apply()
    {
        owner.ApiKey = Restore(owner.ApiKey);
        owner.ModelProviderTemplateId = Restore(owner.ModelProviderTemplateId);
        owner.ModelDefinitionTemplateId = Restore(owner.ModelDefinitionTemplateId);

        owner.ApplyTemplate(ModelProviderTemplate);
        owner.ApplyTemplate(ModelDefinitionTemplate);
    }

    public override Assistant ResolveAssistant(ModelSpecializations specialization)
    {
        if (specialization == ModelSpecializations.Default || owner.Specializations.HasFlag(specialization))
        {
            // If the current assistant already has the specialization, return it directly.
            return owner;
        }

        if (ModelProviderTemplate is { } modelProviderTemplate &&
            modelProviderTemplate.ModelDefinitions.FirstOrDefault(m => m.Specializations.HasFlag(specialization)) is { } modelDefinitionTemplate)
        {
            var systemAssistant = new SystemAssistant(specialization)
            {
                ApiKey = owner.ApiKey,
                ConfiguratorType = AssistantConfiguratorType.PresetBased
            };
            systemAssistant.ApplyTemplate(modelProviderTemplate);
            systemAssistant.ApplyTemplate(modelDefinitionTemplate);
            return systemAssistant;
        }

        // Not found, fallback to selected owner
        return owner;
    }
}

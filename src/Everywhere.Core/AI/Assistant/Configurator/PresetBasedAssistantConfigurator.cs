using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;
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
    [HiddenSettingsItem]
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
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsSelectionItem(nameof(ModelProviderTemplates), DataTemplateKey = typeof(ModelProviderTemplate))]
    public ModelProviderTemplate? ModelProviderTemplate
    {
        get => ModelProviderTemplates.FirstOrDefault(t => t.Id == ModelProviderTemplateId);
        set => ModelProviderTemplateId = value?.Id;
    }

    [HiddenSettingsItem]
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
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ApiKey_Header,
        LocaleKey.CustomAssistant_ApiKey_Description)]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        new ApiKeyComboBox(ServiceLocator.Resolve<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = new Binding(nameof(ApiKey))
            {
                Source = this,
                Mode = BindingMode.TwoWay
            },
            [!ApiKeyComboBox.DefaultNameProperty] = new Binding($"{nameof(ModelProviderTemplate)}.{nameof(ModelProviderTemplate.DisplayName)}")
            {
                Source = this,
            },
        });

    [JsonIgnore]
    [HiddenSettingsItem]
    private IEnumerable<ModelDefinitionTemplate> ModelDefinitionTemplates => ModelProviderTemplate?.ModelDefinitions ?? [];

    [HiddenSettingsItem]
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
    [DynamicResourceKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
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
        if (owner.Specializations.HasFlag(specialization))
        {
            // If the current assistant already has the specialization, return it directly.
            return owner;
        }

        if (ModelProviderTemplate is { } modelProviderTemplate &&
            modelProviderTemplate.ModelDefinitions.FirstOrDefault(m => m.Specializations.HasFlag(specialization)) is { } modelDefinitionTemplate)
        {
            var systemAssistant = new SystemAssistant(specialization) { ApiKey = owner.ApiKey, };
            systemAssistant.ApplyTemplate(modelProviderTemplate);
            systemAssistant.ApplyTemplate(modelDefinitionTemplate);
            return systemAssistant;
        }

        // Not found, fallback to selected owner
        return owner;
    }
}
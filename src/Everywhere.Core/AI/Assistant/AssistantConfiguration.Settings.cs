using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

public sealed partial class OfficialAssistantConfiguration
{
    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(LocaleKey.Empty)]
    public SettingsControl<OfficialModelDefinitionSelector> ModelDefinitionSelector =>
        new(serviceProvider => new OfficialModelDefinitionSelector(serviceProvider, this));
}

public sealed partial class PresetAssistantConfiguration
{
    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public ModelProviderTemplate? ModelProviderTemplate =>
        PresetModelTemplates.Providers.FirstOrDefault(template => template.Id == ProviderId);

    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelProviderTemplate_Header,
        LocaleKey.CustomAssistant_ModelProviderTemplate_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<PresetModelProviderSelector> ModelProviderSelector =>
        new(serviceProvider => new PresetModelProviderSelector(
            this,
            serviceProvider.GetRequiredService<AssistantCatalog>()));

    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(
        LocaleKey.Assistant_ApiKey_Header,
        LocaleKey.Assistant_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(serviceProvider =>
        new ApiKeyComboBox(serviceProvider.GetRequiredService<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (PresetAssistantConfiguration configuration) => configuration.ApiKey,
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

    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Header,
        LocaleKey.CustomAssistant_ModelDefinitionTemplate_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<PresetModelDefinitionSelector> ModelDefinitionSelector =>
        new(serviceProvider => new PresetModelDefinitionSelector(
            serviceProvider.GetRequiredService<IPresetModelProvider>(),
            serviceProvider.GetRequiredService<AssistantCatalog>(),
            this));
}

public sealed partial class AdvancedAssistantConfiguration
{
    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(LocaleKey.Assistant_Endpoint_Header, LocaleKey.Assistant_Endpoint_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<PreviewEndpointTextBox> PreviewEndpointControl => new(_ =>
        new PreviewEndpointTextBox
        {
            MinWidth = 320d,
            [!PreviewEndpointTextBox.EndpointProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfiguration configuration) => configuration.Endpoint,
                source: this,
                mode: BindingMode.TwoWay),
            [!PreviewEndpointTextBox.SchemaProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfiguration configuration) => configuration.Schema,
                source: this,
                mode: BindingMode.OneWay)
        });

    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(LocaleKey.Assistant_ApiKey_Header, LocaleKey.Assistant_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(serviceProvider =>
        new ApiKeyComboBox(serviceProvider.GetRequiredService<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfiguration configuration) => configuration.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    [JsonIgnore]
    [MapperIgnore]
    [DynamicLocaleKey(LocaleKey.Assistant_InputModalities_Header, LocaleKey.Assistant_InputModalities_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(_ =>
        new ModalitiesSelector
        {
            [!ModalitiesSelector.ModalitiesProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfiguration configuration) => configuration.InputModalities,
                source: this,
                mode: BindingMode.TwoWay)
        });
}
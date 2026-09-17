using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Configuration;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.AI.Configurator;

/// <summary>
/// Configurator for advanced model providers.
/// </summary>
[GeneratedSettingsItems]
public sealed partial class AdvancedAssistantConfigurator(Assistant owner) : AssistantConfigurator
{
    [SettingsItemIgnore]
    [CustomValidation(typeof(AdvancedAssistantConfigurator), nameof(ValidateEndpoint))]
    public string? Endpoint
    {
        get => owner.Configuration.Endpoint;
        set
        {
            if (owner.Configuration.Endpoint == value) return;

            ValidateProperty(value);
            owner.Configuration.Endpoint = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(LocaleKey.Assistant_Endpoint_Header, LocaleKey.Assistant_Endpoint_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<PreviewEndpointTextBox> PreviewEndpointControl => new(
        new PreviewEndpointTextBox
        {
            MinWidth = 320d,
            [!PreviewEndpointTextBox.EndpointProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfigurator x) => x.Endpoint,
                source: this,
                mode: BindingMode.TwoWay),
            [!PreviewEndpointTextBox.SchemaProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfigurator x) => x.Schema,
                source: this,
                mode: BindingMode.OneWay)
        });

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
    [DynamicLocaleKey(LocaleKey.Assistant_ApiKey_Header, LocaleKey.Assistant_ApiKey_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ApiKeyComboBox> ApiKeyControl => new(
        serviceProvider => new ApiKeyComboBox(serviceProvider.GetRequiredService<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfigurator x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    [DynamicLocaleKey(LocaleKey.Assistant_Schema_Header, LocaleKey.Assistant_Schema_Description)]
    [SettingsItem(Group = "_")]
    public ModelProviderSchema Schema
    {
        get => owner.Configuration.Schema;
        set
        {
            if (owner.Configuration.Schema == value) return;

            owner.Configuration.Schema = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(LocaleKey.Assistant_ModelId_Header, LocaleKey.Assistant_ModelId_Description)]
    [SettingsItem(Group = "_")]
    [Required, MinLength(1)]
    public string? ModelId
    {
        get => owner.Configuration.ModelId;
        set => owner.Configuration.ModelId = value;
    }

    [DynamicLocaleKey(LocaleKey.Assistant_SupportsToolCall_Header, LocaleKey.Assistant_SupportsToolCall_Description)]
    [SettingsItem(Group = "_")]
    public bool SupportsToolCall
    {
        get => owner.Configuration.SupportsToolCall;
        set => owner.Configuration.SupportsToolCall = value;
    }

    [DynamicLocaleKey(LocaleKey.Assistant_InputModalities_Header, LocaleKey.Assistant_InputModalities_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(
        new ModalitiesSelector
        {
            [!ModalitiesSelector.ModalitiesProperty] = CompiledBinding.Create(
                (AssistantConfiguration x) => x.InputModalities,
                source: owner.Configuration,
                mode: BindingMode.TwoWay)
        });

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.Assistant_ContextLimit_Header, LocaleKey.Assistant_ContextLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int ContextLimit
    {
        get => owner.Configuration.ContextLimit;
        set => owner.Configuration.ContextLimit = value;
    }

    /// <summary>
    /// Maximum number of tokens that the model can output in a single request.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.Assistant_OutputLimit_Header, LocaleKey.Assistant_OutputLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int OutputLimit
    {
        get => owner.Configuration.OutputLimit;
        set => owner.Configuration.OutputLimit = value;
    }

    internal override void NotifyConfigurationChanged(AssistantConfiguration previous, AssistantConfiguration current)
    {
        if (previous.Endpoint != current.Endpoint) OnPropertyChanged(nameof(Endpoint));
        if (previous.ApiKey != current.ApiKey) OnPropertyChanged(nameof(ApiKey));
        if (previous.Schema != current.Schema) OnPropertyChanged(nameof(Schema));
        if (previous.ModelId != current.ModelId) OnPropertyChanged(nameof(ModelId));
        if (previous.SupportsToolCall != current.SupportsToolCall) OnPropertyChanged(nameof(SupportsToolCall));
        if (previous.ContextLimit != current.ContextLimit) OnPropertyChanged(nameof(ContextLimit));
        if (previous.OutputLimit != current.OutputLimit) OnPropertyChanged(nameof(OutputLimit));
    }

    internal override void NotifyConfigurationChanged(string? propertyName)
    {
        var exposedProperty = propertyName switch
        {
            nameof(AssistantConfiguration.Endpoint) => nameof(Endpoint),
            nameof(AssistantConfiguration.ApiKey) => nameof(ApiKey),
            nameof(AssistantConfiguration.Schema) => nameof(Schema),
            nameof(AssistantConfiguration.ModelId) => nameof(ModelId),
            nameof(AssistantConfiguration.SupportsToolCall) => nameof(SupportsToolCall),
            nameof(AssistantConfiguration.ContextLimit) => nameof(ContextLimit),
            nameof(AssistantConfiguration.OutputLimit) => nameof(OutputLimit),
            _ => null
        };
        if (exposedProperty is not null) OnPropertyChanged(exposedProperty);
    }

    public override Assistant ResolveAssistant(ModelSpecializations specialization) => owner;

    public static ValidationResult? ValidateEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new ValidationResult(LocaleResolver.AdvancedAssistantConfigurator_InvalidEndpoint);
        }

        return ValidationResult.Success;
    }
}

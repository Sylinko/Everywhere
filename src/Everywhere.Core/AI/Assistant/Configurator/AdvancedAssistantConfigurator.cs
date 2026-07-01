using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Avalonia.Data;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Views;

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
        get => owner.Endpoint;
        set
        {
            if (owner.Endpoint == value) return;

            ValidateProperty(value);
            owner.Endpoint = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(
        LocaleKey.Assistant_Endpoint_Header,
        LocaleKey.Assistant_Endpoint_Description)]
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
        new ApiKeyComboBox(ServiceLocator.Resolve<Settings>().Model.ApiKeys)
        {
            [!ApiKeyComboBox.SelectedIdProperty] = CompiledBinding.Create(
                (AdvancedAssistantConfigurator x) => x.ApiKey,
                source: this,
                mode: BindingMode.TwoWay)
        });

    [DynamicLocaleKey(
        LocaleKey.Assistant_Schema_Header,
        LocaleKey.Assistant_Schema_Description)]
    [SettingsItem(Group = "_")]
    public ModelProviderSchema Schema
    {
        get => owner.Schema;
        set
        {
            if (owner.Schema == value) return;

            owner.Schema = value;
            OnPropertyChanged();
        }
    }

    [DynamicLocaleKey(
        LocaleKey.Assistant_ModelId_Header,
        LocaleKey.Assistant_ModelId_Description)]
    [SettingsItem(Group = "_")]
    [Required, MinLength(1)]
    public string? ModelId
    {
        get => owner.ModelId;
        set => owner.ModelId = value;
    }

    [DynamicLocaleKey(
        LocaleKey.Assistant_SupportsToolCall_Header,
        LocaleKey.Assistant_SupportsToolCall_Description)]
    [SettingsItem(Group = "_")]
    public bool SupportsToolCall
    {
        get => owner.SupportsToolCall;
        set => owner.SupportsToolCall = value;
    }

    [DynamicLocaleKey(
        LocaleKey.Assistant_InputModalities_Header,
        LocaleKey.Assistant_InputModalities_Description)]
    [SettingsItem(Group = "_")]
    public SettingsControl<ModalitiesSelector> InputModalitiesSelector => new(
        new ModalitiesSelector
        {
            [!ModalitiesSelector.ModalitiesProperty] = CompiledBinding.Create(
                (Assistant x) => x.InputModalities,
                source: owner,
                mode: BindingMode.TwoWay)
        });

    /// <summary>
    /// Maximum number of tokens that the model can process in a single request.
    /// </summary>
    [DynamicLocaleKey(
        LocaleKey.Assistant_ContextLimit_Header,
        LocaleKey.Assistant_ContextLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int ContextLimit
    {
        get => owner.ContextLimit;
        set => owner.ContextLimit = value;
    }

    /// <summary>
    /// Maximum number of tokens that the model can output in a single request.
    /// </summary>
    [DynamicLocaleKey(
        LocaleKey.Assistant_OutputLimit_Header,
        LocaleKey.Assistant_OutputLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public int OutputLimit
    {
        get => owner.OutputLimit;
        set => owner.OutputLimit = value;
    }

    public override void Backup()
    {
        Backup(Schema);
        Backup(Endpoint);
        Backup(ModelId);
        Backup(SupportsToolCall);
        Backup(owner.InputModalities);
        Backup(owner.OutputModalities);
        Backup(ContextLimit);
        Backup(OutputLimit);
    }

    public override void Apply()
    {
        owner.ModelProviderTemplateId = null;
        owner.ModelDefinitionTemplateId = null;

        Schema = Restore(Schema);
        Endpoint = Restore(Endpoint);
        ModelId = Restore(ModelId);
        SupportsToolCall = Restore(SupportsToolCall);
        owner.InputModalities = Restore(owner.InputModalities);
        owner.OutputModalities = Restore(owner.OutputModalities);
        ContextLimit = Restore(ContextLimit);
        OutputLimit = Restore(OutputLimit);
    }

    /// <summary>
    /// For advanced configurator, we will directly use the owner as the assistant, and the specialization is determined by the user input. So we can just return the owner here.
    /// </summary>
    /// <param name="specialization"></param>
    /// <returns></returns>
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
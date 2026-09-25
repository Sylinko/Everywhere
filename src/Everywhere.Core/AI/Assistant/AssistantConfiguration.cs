using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Common;
using Everywhere.Configuration;
using Riok.Mapperly.Abstractions;

namespace Everywhere.AI;

/// <summary>
/// Self-contained model and connection configuration. Loading it never requires a model catalog.
/// </summary>
[SettingsSerializedSubtree]
[JsonPolymorphic]
[JsonDerivedType(typeof(OfficialAssistantConfiguration), "official")]
[JsonDerivedType(typeof(PresetAssistantConfiguration), "preset")]
[JsonDerivedType(typeof(AdvancedAssistantConfiguration), "advanced")]
public abstract partial class AssistantConfiguration : ObservableValidator, IModelDefinition, IHaveSettingsItems, ISyncRoot
{
    [ObservableProperty]
    [NotifyDataErrorInfo]
    [CustomValidation(typeof(AssistantConfiguration), nameof(ValidateEndpoint))]
    [SettingsItemIgnore]
    public partial string? Endpoint { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.Assistant_Schema_Header, LocaleKey.Assistant_Schema_Description)]
    [SettingsItem(Group = "_")]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Guid ApiKey { get; set; }

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required, MinLength(1)]
    [DynamicLocaleKey(LocaleKey.Assistant_ModelId_Header, LocaleKey.Assistant_ModelId_Description)]
    [SettingsItem(Group = "_")]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial string? Name { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.Assistant_SupportsToolCall_Header, LocaleKey.Assistant_SupportsToolCall_Description)]
    [SettingsItem(Group = "_")]
    public partial bool SupportsToolCall { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Modalities InputModalities { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial Modalities OutputModalities { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.Assistant_ContextLimit_Header, LocaleKey.Assistant_ContextLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public partial int ContextLimit { get; set; }

    [ObservableProperty]
    [DynamicLocaleKey(LocaleKey.Assistant_OutputLimit_Header, LocaleKey.Assistant_OutputLimit_Description)]
    [SettingsItem(Group = "_")]
    [SettingsIntegerItem(IsSliderVisible = false)]
    public partial int OutputLimit { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial ModelSpecializations Specializations { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial DateOnly? DeprecationDate { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial DateOnly? ReleaseDate { get; set; }

    [ObservableProperty]
    [SettingsItemIgnore]
    public partial DateOnly? KnowledgeCutoff { get; set; }

    [JsonIgnore]
    [SettingsItemIgnore]
    [MapperIgnore]
    public abstract SettingsItems SettingsItems { get; }

    /// <summary>Applies the selected model, or clears model metadata when the new provider has no models.</summary>
    /// <remarks>The caller must hold this configuration's synchronization lock.</remarks>
    public void Apply(ModelDefinitionTemplate? model)
    {
        ModelId = model?.ModelId;
        Name = model?.Name;
        SupportsToolCall = model?.SupportsToolCall ?? false;
        InputModalities = model?.InputModalities ?? Modalities.None;
        OutputModalities = model?.OutputModalities ?? Modalities.None;
        ContextLimit = model?.ContextLimit ?? 0;
        OutputLimit = model?.OutputLimit ?? 0;
        Specializations = model?.Specializations ?? ModelSpecializations.Default;
        DeprecationDate = model?.DeprecationDate;
        ReleaseDate = model?.ReleaseDate;
        KnowledgeCutoff = model?.KnowledgeCutoff;
    }

    public ModelDefinitionTemplate ToTemplate() => new()
    {
        ModelId = ModelId ?? string.Empty,
        Name = Name ?? ModelId,
        SupportsToolCall = SupportsToolCall,
        InputModalities = InputModalities,
        OutputModalities = OutputModalities,
        ContextLimit = ContextLimit,
        OutputLimit = OutputLimit,
        Specializations = Specializations,
        DeprecationDate = DeprecationDate,
        ReleaseDate = ReleaseDate,
        KnowledgeCutoff = KnowledgeCutoff
    };

    /// <summary>Applies all configuration fields owned by a catalog item.</summary>
    /// <remarks>The caller must hold this configuration's synchronization lock.</remarks>
    public virtual void Apply(AssistantCatalogItem catalogItem)
    {
        Schema = catalogItem.Schema;
        Apply(catalogItem.Model);
    }

    public bool Validate()
    {
        ValidateAllProperties();
        return !HasErrors;
    }

    public static ValidationResult? ValidateEndpoint(string? endpoint, ValidationContext context)
    {
        if (context.ObjectInstance is not AdvancedAssistantConfiguration) return ValidationResult.Success;
        if (string.IsNullOrWhiteSpace(endpoint))
            return new ValidationResult(LocaleResolver.ValidationErrorMessage_Required);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return new ValidationResult(LocaleResolver.AdvancedAssistantConfigurator_InvalidEndpoint);

        return ValidationResult.Success;
    }
}

[GeneratedSettingsItems(IncludeInheritedMembers = false)]
public sealed partial class OfficialAssistantConfiguration : AssistantConfiguration;

[GeneratedSettingsItems(IncludeInheritedMembers = false)]
public sealed partial class PresetAssistantConfiguration : AssistantConfiguration
{
    [ObservableProperty]
    [SettingsItemIgnore]
    [NotifyPropertyChangedFor(nameof(ModelProviderTemplate))]
    public partial string? ProviderId { get; set; }

    public override void Apply(AssistantCatalogItem catalogItem)
    {
        base.Apply(catalogItem);
        Endpoint = catalogItem.Endpoint;
    }
}

[GeneratedSettingsItems]
public sealed partial class AdvancedAssistantConfiguration : AssistantConfiguration;
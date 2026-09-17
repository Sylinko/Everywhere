using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Self-contained model and connection configuration. Loading it never requires a model catalog.
/// </summary>
[SettingsSerializedSubtree]
[JsonPolymorphic]
[JsonDerivedType(typeof(OfficialAssistantConfiguration), "official")]
[JsonDerivedType(typeof(PresetAssistantConfiguration), "preset")]
[JsonDerivedType(typeof(AdvancedAssistantConfiguration), "advanced")]
public abstract partial class AssistantConfiguration : ObservableObject, IModelDefinition
{
    [ObservableProperty]
    public partial string? Endpoint { get; set; }

    [ObservableProperty]
    public partial ModelProviderSchema Schema { get; set; }

    [ObservableProperty]
    public partial Guid ApiKey { get; set; }

    [ObservableProperty]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    public partial string? Name { get; set; }

    [ObservableProperty]
    public partial bool SupportsToolCall { get; set; }

    [ObservableProperty]
    public partial Modalities InputModalities { get; set; }

    [ObservableProperty]
    public partial Modalities OutputModalities { get; set; }

    [ObservableProperty]
    public partial int ContextLimit { get; set; }

    [ObservableProperty]
    public partial int OutputLimit { get; set; }

    [ObservableProperty]
    public partial ModelSpecializations Specializations { get; set; }

    [ObservableProperty]
    public partial DateOnly? DeprecationDate { get; set; }

    [ObservableProperty]
    public partial DateOnly? ReleaseDate { get; set; }

    [ObservableProperty]
    public partial DateOnly? KnowledgeCutoff { get; set; }

    public void Apply(ModelDefinitionTemplate model)
    {
        ModelId = model.ModelId;
        Name = model.Name;
        SupportsToolCall = model.SupportsToolCall;
        InputModalities = model.InputModalities;
        OutputModalities = model.OutputModalities;
        ContextLimit = model.ContextLimit;
        OutputLimit = model.OutputLimit;
        Specializations = model.Specializations;
        DeprecationDate = model.DeprecationDate;
        ReleaseDate = model.ReleaseDate;
        KnowledgeCutoff = model.KnowledgeCutoff;
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
}

public sealed class OfficialAssistantConfiguration : AssistantConfiguration;

public sealed partial class PresetAssistantConfiguration : AssistantConfiguration
{
    [ObservableProperty]
    public partial string? ProviderId { get; set; }
}

public sealed class AdvancedAssistantConfiguration : AssistantConfiguration;
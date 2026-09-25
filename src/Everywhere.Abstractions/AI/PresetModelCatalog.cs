using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Everywhere.AI;

public enum PresetModelSourceStatus
{
    Unknown,
    Available,
    Missing,
    Unmappable
}

public sealed class PresetModelProviderCatalog(
    string providerId,
    ModelCatalogSnapshot<ModelDefinitionTemplate, string> models,
    PresetModelSourceStatus sourceStatus,
    IReadOnlySet<string> sourceModelIds
)
{
    public string ProviderId { get; } = providerId;

    public IReadOnlyList<ModelDefinitionTemplate> Models => ModelSnapshot.Definitions;

    public PresetModelSourceStatus SourceStatus { get; } = sourceStatus;

    public IReadOnlySet<string> SourceModelIds { get; } = sourceModelIds.ToFrozenSet(StringComparer.Ordinal);

    public ModelCatalogSnapshot<ModelDefinitionTemplate, string> ModelSnapshot { get; } = models;

    public bool TryGetModel(string modelId, [NotNullWhen(true)] out ModelDefinitionTemplate? model) =>
        ModelSnapshot.TryGetDefinition(modelId, out model);
}

public sealed class PresetModelCatalog
{
    public bool IsValidated { get; }

    public static PresetModelCatalog Empty { get; } = new([], false);

    private readonly IReadOnlyDictionary<string, PresetModelProviderCatalog> _providersById;

    public PresetModelCatalog(IEnumerable<PresetModelProviderCatalog> providers, bool isValidated)
    {
        IsValidated = isValidated;
        _providersById = providers.ToFrozenDictionary(static provider => provider.ProviderId, StringComparer.Ordinal);
    }

    public PresetModelProviderCatalog? GetProvider(string? providerId) =>
        providerId is not null && _providersById.TryGetValue(providerId, out var provider) ? provider : null;

    public IReadOnlyList<ModelDefinitionTemplate> GetModels(string? providerId) =>
        GetProvider(providerId)?.Models ?? [];
}
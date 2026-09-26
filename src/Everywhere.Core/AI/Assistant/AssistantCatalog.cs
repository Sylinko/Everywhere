using Everywhere.Cloud;

namespace Everywhere.AI;

/// <summary>
/// Provides the assistant-facing view of the official and preset catalogs. It is the single place
/// that interprets catalog authority, missing source entries, and the fallback row for a persisted
/// selection that is no longer listed.
/// </summary>
public sealed class AssistantCatalog(IPresetModelProvider presetModels, IOfficialModelProvider officialModels)
{
    /// <summary>
    /// Resolves the selected model without constructing presentation rows for a selector.
    /// </summary>
    public AssistantModelResolution ResolveModel(AssistantConfiguration configuration)
    {
        lock (configuration)
        {
            return ResolveModelCore(configuration);
        }
    }

    /// <summary>
    /// Finds the first catalog model that provides a requested system-task specialization.
    /// </summary>
    public AssistantModelResolution ResolveSpecializedModel(AssistantConfiguration configuration, ModelSpecializations specialization)
    {
        lock (configuration)
        {
            return configuration switch
            {
                OfficialAssistantConfiguration => ResolveOfficialSpecializedModel(specialization),
                PresetAssistantConfiguration preset => ResolvePresetSpecializedModel(preset, specialization),
                _ => AssistantModelResolution.Empty
            };
        }
    }

    public AssistantCatalogSelection Resolve(AssistantConfiguration configuration)
    {
        lock (configuration)
        {
            return configuration switch
            {
                OfficialAssistantConfiguration official => ResolveOfficial(official),
                PresetAssistantConfiguration preset => ResolvePreset(preset),
                _ => AssistantCatalogSelection.Empty
            };
        }
    }

    public ModelAvailability EvaluateAvailability(AssistantConfiguration configuration, DateOnly today)
    {
        lock (configuration)
        {
            var resolution = ResolveModelCore(configuration);
            if (resolution.SignInRequired && !configuration.ModelId.IsNullOrWhiteSpace())
            {
                return new ModelAvailability(ModelAvailabilityKind.SignInRequired, configuration.ModelId, null);
            }
            if (!resolution.CatalogIsAuthoritative)
            {
                return new ModelAvailability(ModelAvailabilityKind.None, configuration.ModelId, configuration.DeprecationDate);
            }

            return ModelAvailability.Evaluate(
                configuration,
                resolution.Model,
                resolution.MissingIsAuthoritative,
                today);
        }
    }

    /// <summary>
    /// Observes availability for one fixed configuration instance.
    /// </summary>
    public ModelAvailabilityObservation ObserveAvailability(AssistantConfiguration configuration) =>
        new(this, configuration, presetModels, officialModels);

    public void ApplyPresetProvider(PresetAssistantConfiguration configuration, ModelProviderTemplate provider)
    {
        lock (configuration)
        {
            configuration.ProviderId = provider.Id;
            configuration.Endpoint = provider.Endpoint;
            configuration.Schema = provider.Schema;

            var models = presetModels.Catalog.GetModels(provider.Id);
            configuration.Apply(models.FirstOrDefault(static model => model.IsDefault) ?? models.FirstOrDefault());
        }
    }

    public bool Synchronize(AssistantConfiguration configuration)
    {
        lock (configuration)
        {
            var current = ResolveModelCore(configuration);
            if (current.Model is not { } model || current.Schema is not { } schema) return false;

            configuration.Schema = schema;
            configuration.Apply(model);
            if (configuration is PresetAssistantConfiguration preset) preset.Endpoint = current.Endpoint;
            return true;
        }
    }

    private AssistantModelResolution ResolveModelCore(AssistantConfiguration configuration) => configuration switch
    {
        OfficialAssistantConfiguration official => ResolveOfficialModel(official),
        PresetAssistantConfiguration preset => ResolvePresetModel(preset),
        _ => AssistantModelResolution.Empty
    };

    private AssistantModelResolution ResolveOfficialModel(OfficialAssistantConfiguration configuration)
    {
        var catalog = officialModels.Catalog;
        var definition = catalog.GetDefinition(configuration.ModelId);
        var hasModelId = !configuration.ModelId.IsNullOrWhiteSpace();
        return new AssistantModelResolution(
            definition?.Model,
            definition?.Schema,
            null,
            catalog.IsAuthoritative,
            catalog.IsAuthoritative && hasModelId && definition is null,
            officialModels.AccessStatus == OfficialModelCatalogAccessStatus.SignInRequired);
    }

    private AssistantModelResolution ResolvePresetModel(PresetAssistantConfiguration configuration)
    {
        var catalog = presetModels.Catalog;
        var provider = catalog.GetProvider(configuration.ProviderId);
        var model = configuration.ModelId is { } modelId && provider is not null && provider.TryGetModel(modelId, out var definition) ?
            definition :
            null;
        var providerTemplate = configuration.ModelProviderTemplate;
        var hasModelId = !configuration.ModelId.IsNullOrWhiteSpace();
        var missingIsAuthoritative = catalog.IsValidated && hasModelId && model is null && provider switch
        {
            null => true,
            { SourceStatus: PresetModelSourceStatus.Missing } => true,
            { SourceStatus: PresetModelSourceStatus.Available } when configuration.ModelId is { } selectedModelId =>
                !provider.SourceModelIds.Contains(selectedModelId),
            _ => false
        };
        return new AssistantModelResolution(
            model,
            providerTemplate?.Schema ?? configuration.Schema,
            providerTemplate?.Endpoint ?? configuration.Endpoint,
            catalog.IsValidated,
            missingIsAuthoritative,
            false);
    }

    private AssistantModelResolution ResolveOfficialSpecializedModel(ModelSpecializations specialization)
    {
        var catalog = officialModels.Catalog;
        var definition = catalog.Definitions.AsValueEnumerable()
            .FirstOrDefault(candidate => candidate.Model.Specializations.HasFlag(specialization));
        return definition is null ?
            AssistantModelResolution.Empty :
            new AssistantModelResolution(
                definition.Model,
                definition.Schema,
                null,
                catalog.IsAuthoritative,
                false,
                officialModels.AccessStatus == OfficialModelCatalogAccessStatus.SignInRequired);
    }

    private AssistantModelResolution ResolvePresetSpecializedModel(
        PresetAssistantConfiguration configuration,
        ModelSpecializations specialization)
    {
        var catalog = presetModels.Catalog;
        var provider = catalog.GetProvider(configuration.ProviderId);
        var model = provider?.Models.AsValueEnumerable()
            .FirstOrDefault(candidate => candidate.Specializations.HasFlag(specialization));
        if (model is null) return AssistantModelResolution.Empty;

        var providerTemplate = configuration.ModelProviderTemplate;
        return new AssistantModelResolution(
            model,
            providerTemplate?.Schema ?? configuration.Schema,
            providerTemplate?.Endpoint ?? configuration.Endpoint,
            catalog.IsValidated,
            false,
            false);
    }

    private AssistantCatalogSelection ResolveOfficial(OfficialAssistantConfiguration configuration)
    {
        const string ScopeId = "official";
        var catalog = officialModels.Catalog;
        var resolution = ResolveOfficialModel(configuration);
        var items = catalog.Definitions
            .AsValueEnumerable()
            .Select(definition => new AssistantCatalogItem(
                ScopeId,
                definition.Model,
                definition.Schema,
                null,
                false))
            .ToList();
        var catalogItem = Find(items, resolution.Model?.ModelId);
        var selectedItem = catalogItem ?? CreateFallback(ScopeId, configuration, configuration.Schema, null);
        if (catalogItem is null && selectedItem is not null) items.Insert(0, selectedItem);

        return new AssistantCatalogSelection(
            items,
            selectedItem,
            catalogItem,
            resolution.MissingIsAuthoritative);
    }

    private AssistantCatalogSelection ResolvePreset(PresetAssistantConfiguration configuration)
    {
        var catalog = presetModels.Catalog;
        var provider = catalog.GetProvider(configuration.ProviderId);
        var resolution = ResolvePresetModel(configuration);
        var providerTemplate = configuration.ModelProviderTemplate;
        var scopeId = configuration.ProviderId ?? string.Empty; // TODO: scopeId? Empty?
        var schema = providerTemplate?.Schema ?? configuration.Schema;
        var endpoint = providerTemplate?.Endpoint ?? configuration.Endpoint;
        var items = (provider?.Models ?? [])
            .AsValueEnumerable()
            .Select(model => new AssistantCatalogItem(scopeId, model, schema, endpoint, false))
            .ToList();
        var catalogItem = Find(items, resolution.Model?.ModelId);
        var selectedItem = catalogItem ?? CreateFallback(scopeId, configuration, schema, endpoint);
        if (catalogItem is null && selectedItem is not null) items.Insert(0, selectedItem);

        return new AssistantCatalogSelection(
            items,
            selectedItem,
            catalogItem,
            resolution.MissingIsAuthoritative);
    }

    private static AssistantCatalogItem? Find(IEnumerable<AssistantCatalogItem> items, string? modelId)
    {
        return modelId is null ? null : items.AsValueEnumerable().FirstOrDefault(item => item.ModelId == modelId);
    }

    private static AssistantCatalogItem? CreateFallback(
        string scopeId,
        AssistantConfiguration configuration,
        ModelProviderSchema schema,
        string? endpoint)
    {
        return configuration.ModelId.IsNullOrWhiteSpace() ?
            null :
            new AssistantCatalogItem(scopeId, configuration.ToTemplate(), schema, endpoint, true);
    }
}

/// <summary>
/// The catalog result for one configured model. Unlike <see cref="AssistantCatalogSelection"/>,
/// this value has no selector rows or historical fallback item.
/// </summary>
public sealed record AssistantModelResolution(
    ModelDefinitionTemplate? Model,
    ModelProviderSchema? Schema,
    string? Endpoint,
    bool CatalogIsAuthoritative,
    bool MissingIsAuthoritative,
    bool SignInRequired
)
{
    public static AssistantModelResolution Empty { get; } = new(null, null, null, false, false, false);
}

/// <summary>
/// A stable ComboBox row identity. Catalog metadata may be replaced while Avalonia still finds the
/// corresponding row by scope and model id in the new ItemsSource.
/// </summary>
public sealed record AssistantCatalogItem(
    string ScopeId,
    ModelDefinitionTemplate Model,
    ModelProviderSchema Schema,
    string? Endpoint,
    bool IsFallback
)
{
    public string ModelId => Model.ModelId;

    internal bool HasSameContent(AssistantCatalogItem other) =>
        Equals(Model, other.Model) &&
        Schema == other.Schema &&
        StringComparer.Ordinal.Equals(Endpoint, other.Endpoint) &&
        IsFallback == other.IsFallback;

    public bool Equals(AssistantCatalogItem? other) =>
        other is not null &&
        StringComparer.Ordinal.Equals(ScopeId, other.ScopeId) &&
        StringComparer.Ordinal.Equals(ModelId, other.ModelId);

    public override int GetHashCode() => HashCode.Combine(
        StringComparer.Ordinal.GetHashCode(ScopeId),
        StringComparer.Ordinal.GetHashCode(ModelId));

    public override string ToString() => Model.ToString();
}

public sealed record AssistantCatalogSelection(
    IReadOnlyList<AssistantCatalogItem> Items,
    AssistantCatalogItem? SelectedItem,
    AssistantCatalogItem? CatalogItem,
    bool MissingIsAuthoritative
)
{
    public static AssistantCatalogSelection Empty { get; } = new([], null, null, false);

    public bool IsSelectedModelUnavailable => MissingIsAuthoritative && CatalogItem is null;

    public AssistantCatalogSelection PreserveItems(IReadOnlyList<AssistantCatalogItem> previous)
    {
        if (Items.Count == 0) return this;

        var previousByIdentity = previous.ToDictionary(static item => item);
        var stableItems = Items
            .AsValueEnumerable()
            .Select(item => previousByIdentity.TryGetValue(item, out var current) && current.HasSameContent(item) ? current : item)
            .ToArray();
        var publishedItems = previous.Count == stableItems.Length &&
            previous.SequenceEqual(stableItems, ReferenceEqualityComparer.Instance) ?
                previous :
                stableItems;
        return this with
        {
            Items = publishedItems,
            SelectedItem = FindPublishedItem(publishedItems, SelectedItem),
            CatalogItem = FindPublishedItem(publishedItems, CatalogItem)
        };
    }

    private static AssistantCatalogItem? FindPublishedItem(IEnumerable<AssistantCatalogItem> items, AssistantCatalogItem? target) =>
        target is null ? null : items.AsValueEnumerable().FirstOrDefault(item => item.Equals(target));
}
using Everywhere.Common;
using Microsoft.Extensions.Logging;

namespace Everywhere.AI;

/// <summary>
/// Publishes the models.dev catalog for locally supported providers. Cached definitions remain usable
/// while the source is being revalidated; authority is granted only by the current application session.
/// </summary>
public sealed partial class ModelsDevPresetModelProvider(
    IHttpClientFactory httpClientFactory,
    ILogger<ModelsDevPresetModelProvider> logger
) : ModelCatalogProvider<Dictionary<string, ModelsDevProvider>, ModelsDevPresetModelProvider.PreparedCatalog>(
        new ModelsDevModelCatalogClient(httpClientFactory, MaximumCatalogBytes),
        new ModelsDevCatalogCacheStore(Path.Combine(RuntimeConstants.CacheFolderPath, "models-dev.json"), MaximumCatalogBytes),
        logger),
    IPresetModelProvider,
    IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 1;

    public PresetModelCatalog Catalog => Volatile.Read(ref _catalog);

    public event EventHandler? CatalogChanged;

    protected override int CacheVersion => 1;

    private const long MaximumCatalogBytes = 32 * 1024 * 1024;

    private PresetModelCatalog _catalog = CreateInitialCatalog();

    public Task InitializeAsync()
    {
        RefreshInBackground();
        return Task.CompletedTask;
    }

    protected override PreparedCatalog PrepareCatalog(Dictionary<string, ModelsDevProvider> providers) => ConvertCatalog(providers);

    protected override void ApplyCatalog(PreparedCatalog prepared, bool isAuthoritative)
    {
        if (IsDisposed) return;

        var previous = Catalog;
        var providers = new List<PresetModelProviderCatalog>();
        foreach (var template in PresetModelTemplates.Providers)
        {
            var current = previous.GetProvider(template.Id);
            var source = prepared.Providers[template.Id];
            var definitions = source.SourceStatus switch
            {
                PresetModelSourceStatus.Available => source.Models,
                PresetModelSourceStatus.Missing => [],
                _ => current?.Models ?? SortDefinitions(template.ModelDefinitions)
            };
            var models = ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
                definitions.Select(static model => KeyValuePair.Create(model.ModelId, model)),
                current?.ModelSnapshot);
            providers.Add(
                new PresetModelProviderCatalog(
                    template.Id,
                    models,
                    isAuthoritative ? source.SourceStatus : PresetModelSourceStatus.Unknown,
                    isAuthoritative ? source.SourceModelIds : new HashSet<string>(StringComparer.Ordinal)));
        }

        Volatile.Write(ref _catalog, new PresetModelCatalog(providers, isAuthoritative));
    }

    protected override void ClearPublishedCatalog()
    {
        if (IsDisposed) return;

        Volatile.Write(ref _catalog, CreateInitialCatalog());
    }

    protected override void OnCatalogChanged()
    {
        if (IsDisposed) return;

        OnPropertyChanged(nameof(Catalog));
        RaiseCatalogChanged(CatalogChanged);
    }

    private static PresetModelCatalog CreateInitialCatalog()
    {
        var providers = PresetModelTemplates.Providers.Select(template =>
            new PresetModelProviderCatalog(
                template.Id,
                ModelCatalogSnapshot<ModelDefinitionTemplate, string>.Create(
                    SortDefinitions(template.ModelDefinitions)
                        .Select(static model => KeyValuePair.Create(model.ModelId, model))),
                PresetModelSourceStatus.Unknown,
                new HashSet<string>(StringComparer.Ordinal)));
        return new PresetModelCatalog(providers, false);
    }

    private static ModelDefinitionTemplate[] SortDefinitions(IEnumerable<ModelDefinitionTemplate> definitions) =>
        definitions.OrderBy(static model => model, ModelDefinitionComparer.Shared).ToArray();

    public sealed record PreparedCatalog(IReadOnlyDictionary<string, PreparedProvider> Providers);

    public sealed record PreparedProvider(
        PresetModelSourceStatus SourceStatus,
        IReadOnlyList<ModelDefinitionTemplate> Models,
        IReadOnlySet<string> SourceModelIds
    );
}
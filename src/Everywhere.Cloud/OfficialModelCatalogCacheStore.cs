using Everywhere.AI;
using Everywhere.Configuration;

namespace Everywhere.Cloud;

internal sealed class OfficialModelCatalogCacheStore(PersistentState persistentState) : IModelCatalogCacheStore<OfficialModelDefinition[]>
{
    public ValueTask<ModelCatalogCacheEntry<OfficialModelDefinition[]>?> LoadAsync(CancellationToken cancellationToken)
    {
        var cache = persistentState.OfficialModelCatalog;
        return ValueTask.FromResult(
            cache is null ?
                null :
                new ModelCatalogCacheEntry<OfficialModelDefinition[]>(
                    cache.Version,
                    cache.EntityTag,
                    cache.Definitions));
    }

    public ValueTask SaveAsync(ModelCatalogCacheEntry<OfficialModelDefinition[]> entry, CancellationToken cancellationToken)
    {
        persistentState.OfficialModelCatalog = new OfficialModelCatalogCache(entry.Version, entry.EntityTag, entry.Catalog);
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        persistentState.OfficialModelCatalog = null;
        return ValueTask.CompletedTask;
    }
}
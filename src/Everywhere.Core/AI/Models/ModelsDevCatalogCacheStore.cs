using System.Text.Json;

namespace Everywhere.AI;

internal sealed class ModelsDevCatalogCacheStore(
    string path,
    long maximumCacheBytes
)
    : IModelCatalogCacheStore<Dictionary<string, ModelsDevProvider>>
{
    public async ValueTask<ModelCatalogCacheEntry<Dictionary<string, ModelsDevProvider>>?> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > maximumCacheBytes) return null;

        await using var stream = File.OpenRead(path);
        var envelope = await JsonSerializer.DeserializeAsync(
            stream,
            ModelsDevJsonContext.Default.ModelsDevCacheEnvelope,
            cancellationToken);
        return envelope is null ?
            null :
            new ModelCatalogCacheEntry<Dictionary<string, ModelsDevProvider>>(
                envelope.Version,
                envelope.ETag,
                envelope.Providers);
    }

    public async ValueTask SaveAsync(
        ModelCatalogCacheEntry<Dictionary<string, ModelsDevProvider>> entry,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            var envelope = new ModelsDevCacheEnvelope(entry.Version, entry.EntityTag, entry.Catalog);
            await JsonSerializer.SerializeAsync(
                stream,
                envelope,
                ModelsDevJsonContext.Default.ModelsDevCacheEnvelope,
                cancellationToken);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public ValueTask ClearAsync(CancellationToken cancellationToken)
    {
        File.Delete(path);
        File.Delete(path + ".tmp");
        return ValueTask.CompletedTask;
    }
}
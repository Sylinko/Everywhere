using Everywhere.Common;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Packaging;
using Serilog;

namespace Everywhere.Modding;

/// <summary>
/// Imports user-managed mod sources once at startup and activates immutable runtime copies.
/// Source packages and persistent data stay separate from the rebuildable runtime cache.
/// </summary>
/// <remarks>
/// The existing application startup drain completes before DI releases this service. Normal disposal
/// releases mod-owned resources without reversing application changes or requiring a live dispatcher.
/// </remarks>
public sealed class ModEngineManager(IServiceProvider services) : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// Gets the directory containing original ZIP packages and unpacked package folders.
    /// </summary>
    public static string ModDirectory => Path.Combine(RuntimeConstants.ConfigurationFolderPath, "mods");

    /// <summary>
    /// Gets persistent user data, kept separate from installed code and temporary files.
    /// </summary>
    public static string ModDataDirectory => Path.Combine(RuntimeConstants.ConfigurationFolderPath, "mods-data");

    /// <summary>
    /// Owns the runtime created during startup; the root DI container owns this service, not mod entries.
    /// </summary>
    private ModEngine? _engine;

    #region IAsyncInitializer

    /// <inheritdoc />
    static AsyncInitializerIndex IAsyncInitializer.Index => AsyncInitializerIndex.Startup;

    /// <inheritdoc />
    async Task IAsyncInitializer.InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new ModPackageStore(
                RuntimeConstants.EnsureCacheFolderPath("mods"),
                ModDataDirectory,
                RuntimeConstants.EnsureCacheFolderPath("mods", "staging"));
            var imported = await new ModSourceCatalog(ModDirectory, store)
                .ImportAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var diagnostic in imported.Diagnostics)
            {
                Log.ForContext<ModEngineManager>().Warning("Mod source {Code}: {Detail}", diagnostic.Code, diagnostic.Detail);
            }

            if (imported.Installations.Count == 0) return;

            var references = new HostReferenceSet(AppContext.BaseDirectory);
            var host = references.Descriptor;
            var definitions = new List<ModDefinition>();

            foreach (var installation in imported.Installations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                definitions.Add(
                    new ModDefinition(
                        installation,
                        store.GetDataDirectory(installation),
                        host.RuntimeIdentifier,
                        host.HostVersion,
                        host.ModApiVersion,
                        host.Framework));
            }

            _engine = new ModEngine(new HostAssemblyCatalog(references.Assemblies.Values), services, new ModEngineOptions());

            LogResult("startup", await _engine.ApplyAsync(definitions, cancellationToken).ConfigureAwait(false));
            foreach (var mod in _engine.Mods.Where(mod => mod.SkipReason is not null))
            {
                Log.ForContext<ModEngineManager>().Information("Mod {ModId} skipped: {Reason}.", mod.Id, mod.SkipReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Keep diagnostic text, not exception objects that could retain a collectible assembly.
            Log.ForContext<ModEngineManager>().Error("Mod startup failed: {Detail}", exception.ToString());
        }
    }

    #endregion

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_engine is null) return;

        try
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Failure in an extension must not prevent DI from disposing the remaining host services.
            Log.ForContext<ModEngineManager>().Error("Mod disposal failed: {Detail}", exception.ToString());
        }
    }

    /// <summary>
    /// Records actual activation and cleanup outcomes without introducing a management page.
    /// </summary>
    private static void LogResult(string operation, ModApplyResult result)
    {
        var logger = Log.ForContext<ModEngineManager>();
        logger.Information("Mod {Operation}: {Status}; active mods: {Mods}", operation, result.Status, result.ActiveModIds);

        foreach (var diagnostic in result.Diagnostics)
        {
            logger.Warning("Mod {ModId} {Code}: {Detail}", diagnostic.ModId, diagnostic.Code, diagnostic.Detail);
        }
    }
}

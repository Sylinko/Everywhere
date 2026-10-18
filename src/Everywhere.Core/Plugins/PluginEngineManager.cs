using Everywhere.Common;
using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Packaging;
using Serilog;

namespace Everywhere.Plugins;

/// <summary>
/// Imports user-managed plugin sources once at startup and activates immutable runtime copies.
/// Source packages and persistent data stay separate from installation and cache directories.
/// </summary>
/// <remarks>
/// The existing application startup drain completes before DI releases this service. Normal disposal
/// releases plugin-owned resources without reversing application changes or requiring a live dispatcher.
/// </remarks>
public sealed class PluginEngineManager(IServiceProvider services) : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// Gets the directory containing original ZIP packages and unpacked package folders.
    /// </summary>
    public static string PluginDirectory => Path.Combine(RuntimeConstants.ConfigurationFolderPath, "plugins");

    /// <summary>
    /// Gets persistent user data, kept separate from installed code and temporary files.
    /// </summary>
    public static string PluginDataDirectory => Path.Combine(RuntimeConstants.ConfigurationFolderPath, "plugins-data");

    /// <inheritdoc />
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    /// <summary>
    /// Owns the runtime created during startup; the root DI container owns this service, not plugin entries.
    /// </summary>
    private PluginEngine? _engine;

    /// <inheritdoc />
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var store = new PluginPackageStore(
                RuntimeConstants.EnsureWritableDataFolderPath("installed-plugins"),
                PluginDataDirectory,
                RuntimeConstants.EnsureCacheFolderPath("plugin-staging"));
            var imported = await new PluginSourceCatalog(PluginDirectory, store)
                .ImportAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var diagnostic in imported.Diagnostics)
            {
                Log.ForContext<PluginEngineManager>().Warning("Plugin source {Code}: {Detail}", diagnostic.Code, diagnostic.Detail);
            }

            if (imported.Installations.Count == 0) return;

            var references = new HostReferenceSet(AppContext.BaseDirectory);
            var host = references.Descriptor;
            var definitions = new List<PluginDefinition>();

            foreach (var installation in imported.Installations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                definitions.Add(
                    new PluginDefinition(
                        installation,
                        store.GetDataDirectory(installation),
                        host.RuntimeIdentifier,
                        host.HostVersion,
                        host.PluginApiVersion,
                        host.Framework));
            }

            _engine = new PluginEngine(new HostAssemblyCatalog(references.Assemblies.Values), services, new PluginEngineOptions());

            LogResult("startup", await _engine.ApplyAsync(definitions, cancellationToken).ConfigureAwait(false));
            foreach (var plugin in _engine.Plugins.Where(plugin => plugin.SkipReason is not null))
            {
                Log.ForContext<PluginEngineManager>().Information("Plugin {PluginId} skipped: {Reason}.", plugin.Id, plugin.SkipReason);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Keep diagnostic text, not exception objects that could retain a collectible assembly.
            Log.ForContext<PluginEngineManager>().Error("Plugin startup failed: {Detail}", exception.ToString());
        }
    }

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
            Log.ForContext<PluginEngineManager>().Error("Plugin disposal failed: {Detail}", exception.ToString());
        }
    }

    /// <summary>
    /// Records actual activation and cleanup outcomes without introducing a management page.
    /// </summary>
    private static void LogResult(string operation, PluginApplyResult result)
    {
        var logger = Log.ForContext<PluginEngineManager>();
        logger.Information("Plugin {Operation}: {Status}; active plugins: {Plugins}", operation, result.Status, result.ActivePluginIds);

        foreach (var diagnostic in result.Diagnostics)
        {
            logger.Warning("Plugin {PluginId} {Code}: {Detail}", diagnostic.PluginId, diagnostic.Code, diagnostic.Detail);
        }
    }
}
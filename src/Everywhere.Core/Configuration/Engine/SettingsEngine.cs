using Everywhere.Utilities;
using Microsoft.Extensions.Logging;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Coordinates the JSON-backed settings document and the runtime <see cref="Configuration.Settings"/> object.
/// </summary>
/// <remarks>
/// SettingsEngine is a JSON document patch engine, not an <c>IConfigurationProvider</c>.
/// The JSON document remains the persistence source of truth, while the runtime
/// object graph is patched in place so MVVM references stay stable.
/// </remarks>
public sealed class SettingsEngine : IDisposable
{
    /// <summary>
    /// Gets the runtime settings object used by the rest of the application.
    /// </summary>
    public Settings Settings { get; }

    /// <summary>
    /// Gets the JSON document store used for persistence.
    /// </summary>
    public JsonSettingsStorage Storage { get; }

    /// <summary>
    /// Gets diagnostics reported by the document store and patch binder.
    /// </summary>
    public IEnumerable<SettingsEngineDiagnostic> Diagnostics =>
        Storage.Diagnostics.Concat(_binder.Diagnostics);

    private readonly SettingsPatchBinder _binder;
    private readonly ISettingsDescriptor _rootDescriptor;
    private readonly ObjectObserver _observer;

    /// <summary>
    /// Loads settings from disk, runs JSON patching into a fresh <see cref="Configuration.Settings"/> instance, and returns the engine.
    /// </summary>
    public static SettingsEngine Load(string filePath, IServiceProvider serviceProvider, ILoggerFactory loggerFactory)
    {
        var store = JsonSettingsStorage.Load(filePath, loggerFactory.CreateLogger<JsonSettingsStorage>());
        var engine = new SettingsEngine(new Settings(serviceProvider), store, serviceProvider);
        engine.PatchSettings();
        engine.StartObserveSettings();
        return engine;
    }

    /// <summary>
    /// Creates a settings engine over an existing runtime settings object and JSON document store.
    /// </summary>
    private SettingsEngine(
        Settings settings,
        JsonSettingsStorage storage,
        IServiceProvider serviceProvider,
        SettingsPatchBinder? binder = null)
    {
        Settings = settings;
        Storage = storage;

        _binder = binder ?? new SettingsPatchBinder(serviceProvider);
        _rootDescriptor = _binder.GetDescriptor(typeof(Settings));
        _observer = new ObjectObserver(HandleSettingsChanges);
    }

    private void HandleSettingsChanges(in ObjectObserverChangedEventArgs e)
    {
        _binder.WriteObservedPath(Storage, _rootDescriptor, e.Path, e.Value);
    }

    /// <summary>
    /// Patches the runtime settings object from the current JSON document.
    /// </summary>
    private void PatchSettings() => Storage.Edit(root => _binder.Patch(root, Settings), signalChange: false);

    /// <summary>
    /// Starts observing the runtime settings object for changes and writing them to the JSON document.
    /// </summary>
    private void StartObserveSettings() => _observer.Observe(Settings);

    /// <summary>
    /// Forces pending JSON document changes to disk.
    /// </summary>
    public Task FlushAsync(CancellationToken cancellationToken = default) => Storage.FlushAsync(cancellationToken);

    /// <summary>
    /// Disposes the underlying JSON document store and save loop.
    /// </summary>
    public void Dispose()
    {
        Storage.Dispose();
        _observer.Dispose();
    }
}
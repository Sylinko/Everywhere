using System.ComponentModel;
using System.Text.Json.Nodes;
using Everywhere.Utilities;

namespace Everywhere.Configuration.Engine;

/// <summary>
/// Owns one settings document, its stable model, observation and explicit serialization metadata.
/// Stop all model writers before disposing; final saving does not require a UI dispatcher.
/// </summary>
public sealed class SettingsEngine<TSettings> : IAsyncDisposable where TSettings : class, INotifyPropertyChanged
{
    /// <summary>
    /// Gets the model patched in place from the document.
    /// </summary>
    public TSettings Settings { get; }

    /// <summary>
    /// Gets the document backing this settings model.
    /// </summary>
    public JsonSettingsStorage Storage { get; }

    /// <summary>
    /// Gets current persistence and binding diagnostics, owned by this engine's lifetime.
    /// </summary>
    public IEnumerable<SettingsEngineDiagnostic> Diagnostics => Storage.Diagnostics.Concat(_binder.Diagnostics);

    private readonly SettingsPatchBinder _binder;
    private readonly ISettingsDescriptor _rootDescriptor;
    private readonly DeepObserver _observer;
    private Task? _disposal;

    /// <summary>
    /// Attaches an existing document to a model. Application and plugin entry points prepare their own migrations.
    /// </summary>
    internal SettingsEngine(TSettings settings, JsonSettingsStorage storage, SettingsPatchBinder binder)
    {
        Settings = settings;
        Storage = storage;

        _binder = binder;
        _rootDescriptor = binder.GetDescriptor(typeof(TSettings));
        _observer = new DeepObserver(HandleSettingsChanges);

        Storage.Edit(
            root =>
            {
                var modelRoot = (JsonObject)root.DeepClone();
                // Document metadata belongs to migrations, not to the observable settings model.
                modelRoot.Remove(SettingsEngineJson.VersionPropertyName);
                binder.Patch(modelRoot, settings);
            },
            signalChange: false);
        _observer.Observe(settings);
    }

    /// <summary>
    /// Stops observing before flushing and joining background persistence. Repeated disposal shares the result.
    /// </summary>
    public ValueTask DisposeAsync() => new(_disposal ??= DisposeCoreAsync());

    /// <summary>
    /// Finishes the last save while always releasing the document store on failure.
    /// </summary>
    private async Task DisposeCoreAsync()
    {
        _observer.Dispose();
        try
        {
            await Storage.FlushAsync().ConfigureAwait(false);
        }
        finally
        {
            await Storage.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes observed changes using this engine's metadata without exposing document version as a setting.
    /// </summary>
    private void HandleSettingsChanges(in DeepObserverChangedEventArgs e)
    {
        _binder.WriteObservedPath(Storage, _rootDescriptor, Settings, e.Path, e.Value);
    }
}
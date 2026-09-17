using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Applies validated catalog metadata to live, persisted preset configurations. Model objects created
/// for migration, serialization or a running generation are never implicitly enrolled.
/// </summary>
public sealed class AssistantConfigurationSynchronizer(Settings settings, IPresetModelProvider provider) : IAsyncInitializer, IDisposable
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings + 2;

    private readonly HashSet<Assistant> _tracked = [];
    private readonly Dictionary<Assistant, int> _drafts = [];
    private INotifyCollectionChanged? _collection;
    private bool _started;
    private bool _synchronizing;
    private bool _disposed;

    public Task InitializeAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;
        settings.Model.PropertyChanged += HandleModelSettingsChanged;
        provider.ModelsChanged += HandleModelsChanged;
        ReconcileOwners();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Registers an editor-owned draft until it is either discarded or added to Settings.
    /// The returned registration belongs to the editor, not to the assistant.
    /// </summary>
    public IDisposable TrackDraft(Assistant assistant)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        _drafts[assistant] = _drafts.GetValueOrDefault(assistant) + 1;
        ReconcileOwners();
        return Disposable.Create(() => Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (_disposed) return;
            if (_drafts.TryGetValue(assistant, out var count) && count > 1) _drafts[assistant] = count - 1;
            else _drafts.Remove(assistant);
            ReconcileOwners();
        }));
    }

    private void ReconcileOwners()
    {
        if (_disposed) return;
        if (!ReferenceEquals(_collection, settings.Model.CustomAssistants))
        {
            if (_collection is not null) _collection.CollectionChanged -= HandleAssistantsChanged;
            _collection = settings.Model.CustomAssistants;
            _collection.CollectionChanged += HandleAssistantsChanged;
        }
        var desired = new HashSet<Assistant>(settings.Model.CustomAssistants)
        {
            settings.SystemAssistant.TitleGeneration,
            settings.SystemAssistant.DefaultSubagent,
            settings.SystemAssistant.ImageUnderstanding
        };
        desired.UnionWith(_drafts.Keys);
        foreach (var assistant in _tracked.Where(a => !desired.Contains(a)).ToArray())
        {
            assistant.PropertyChanged -= HandleAssistantChanged;
            _tracked.Remove(assistant);
        }
        foreach (var assistant in desired)
            if (_tracked.Add(assistant)) assistant.PropertyChanged += HandleAssistantChanged;
        Synchronize();
    }

    private void Synchronize()
    {
        if (_disposed || _synchronizing) return;
        _synchronizing = true;
        try
        {
            foreach (var assistant in _tracked)
            {
                if (assistant.Configuration is not PresetAssistantConfiguration preset) continue;
                var definition = provider.GetValidatedModel(preset.ProviderId, preset.ModelId);
                if (definition is null) continue;
                preset.Apply(definition);
            }
        }
        finally { _synchronizing = false; }
    }

    private void HandleModelsChanged(object? sender, EventArgs e) => Dispatcher.UIThread.PostOnDemand(Synchronize);
    private void HandleAssistantsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Dispatcher.UIThread.PostOnDemand(ReconcileOwners);
    private void HandleModelSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ModelSettings.CustomAssistants)) Dispatcher.UIThread.PostOnDemand(ReconcileOwners);
    }
    private void HandleAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Assistant.Configuration)) Dispatcher.UIThread.PostOnDemand(Synchronize);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        settings.Model.PropertyChanged -= HandleModelSettingsChanged;
        provider.ModelsChanged -= HandleModelsChanged;
        if (_collection is not null) _collection.CollectionChanged -= HandleAssistantsChanged;
        foreach (var assistant in _tracked) assistant.PropertyChanged -= HandleAssistantChanged;
        _tracked.Clear();
        _drafts.Clear();
    }
}
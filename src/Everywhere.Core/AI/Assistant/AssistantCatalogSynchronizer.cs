using System.Collections.Specialized;
using System.ComponentModel;
using Everywhere.Cloud;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.AI;

/// <summary>
/// Keeps persisted official and preset assistants aligned with the latest matching catalog entry.
/// Missing entries retain their complete historical configuration so the server remains the final
/// authority when a user continues to use an unlisted model.
/// </summary>
public sealed class AssistantCatalogSynchronizer(
    Settings settings,
    AssistantCatalog catalog,
    IPresetModelProvider presetModels,
    IOfficialModelProvider officialModels
) : IAsyncInitializer, IDisposable
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Network + 2;

    private readonly HashSet<CustomAssistant> _assistants = [];
    private readonly Dictionary<CustomAssistant, AssistantConfiguration> _configurations = [];
    private readonly Lock _assistantsLock = new();
    private INotifyCollectionChanged? _collection;
    private int _disposeState;

    public Task InitializeAsync()
    {
        settings.Model.PropertyChanged += HandleModelSettingsChanged;
        presetModels.CatalogChanged += HandleCatalogChanged;
        officialModels.CatalogChanged += HandleCatalogChanged;
        BindCollection();
        SynchronizeAll();
        return Task.CompletedTask;
    }

    private void BindCollection()
    {
        if (_collection is not null) _collection.CollectionChanged -= HandleAssistantsChanged;
        _collection = settings.Model.CustomAssistants;
        _collection.CollectionChanged += HandleAssistantsChanged;

        CustomAssistant[] previous;
        lock (_assistantsLock)
        {
            previous = [.. _assistants];
        }

        foreach (var assistant in previous) UnbindAssistant(assistant);
        foreach (var assistant in settings.Model.CustomAssistants) BindAssistant(assistant);
    }

    private void BindAssistant(CustomAssistant assistant)
    {
        lock (_assistantsLock)
        {
            if (_disposeState != 0 || !_assistants.Add(assistant)) return;
            assistant.PropertyChanged += HandleAssistantChanged;
            var configuration = assistant.Configuration;
            _configurations.Add(assistant, configuration);
            configuration.PropertyChanged += HandleConfigurationChanged;
        }
    }

    private void UnbindAssistant(CustomAssistant assistant)
    {
        lock (_assistantsLock)
        {
            if (!_assistants.Remove(assistant)) return;
            assistant.PropertyChanged -= HandleAssistantChanged;
            if (_configurations.Remove(assistant, out var configuration))
            {
                configuration.PropertyChanged -= HandleConfigurationChanged;
            }
        }
    }

    private void HandleModelSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModelSettings.CustomAssistants)) return;
        BindCollection();
        SynchronizeAll();
    }

    private void HandleAssistantsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            BindCollection();
            SynchronizeAll();
            return;
        }

        if (e.OldItems is not null)
        {
            foreach (var assistant in e.OldItems.AsValueEnumerable().OfType<CustomAssistant>())
            {
                UnbindAssistant(assistant);
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var assistant in e.NewItems.AsValueEnumerable().OfType<CustomAssistant>())
            {
                BindAssistant(assistant);
                Synchronize(assistant);
            }
        }
    }

    private void HandleAssistantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Assistant.Configuration) && sender is CustomAssistant assistant)
        {
            RebindConfiguration(assistant);
            Synchronize(assistant);
        }
    }

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            not nameof(AssistantConfiguration.ModelId) and
            not nameof(AssistantConfiguration.Schema) and
            not nameof(PresetAssistantConfiguration.ProviderId)) return;

        CustomAssistant? assistant = null;
        lock (_assistantsLock)
        {
            if (_disposeState != 0) return;
            foreach (var pair in _configurations)
            {
                if (!ReferenceEquals(pair.Value, sender)) continue;
                assistant = pair.Key;
                break;
            }
        }

        if (assistant is not null) UpdateReasoningEffortDefaults(assistant);
    }

    private void HandleCatalogChanged(object? sender, EventArgs e) => SynchronizeAll();

    private void SynchronizeAll()
    {
        CustomAssistant[] assistants;
        lock (_assistantsLock)
        {
            if (_disposeState != 0) return;
            assistants = [.. _assistants];
        }

        foreach (var assistant in assistants) Synchronize(assistant);
    }

    private void Synchronize(CustomAssistant assistant)
    {
        if (Volatile.Read(ref _disposeState) != 0) return;

        catalog.Synchronize(assistant.Configuration);
        UpdateReasoningEffortDefaults(assistant);
    }

    private void RebindConfiguration(CustomAssistant assistant)
    {
        lock (_assistantsLock)
        {
            if (_disposeState != 0 || !_assistants.Contains(assistant)) return;

            var current = assistant.Configuration;
            if (_configurations.TryGetValue(assistant, out var previous))
            {
                if (ReferenceEquals(previous, current)) return;
                previous.PropertyChanged -= HandleConfigurationChanged;
            }

            _configurations[assistant] = current;
            current.PropertyChanged += HandleConfigurationChanged;
        }
    }

    private void UpdateReasoningEffortDefaults(CustomAssistant assistant)
    {
        var configuration = assistant.Configuration;
        IReadOnlyList<string>? defaults = null;
        if (configuration is not AdvancedAssistantConfiguration &&
            catalog.ResolveModel(configuration).Model is { ReasoningEffortValues.Count: > 0 } model)
        {
            defaults = model.ReasoningEffortValues;
        }

        if (ReferenceEquals(configuration, assistant.Configuration))
        {
            assistant.EffectiveReasoningOptions?.DefaultReasoningEffortValues = defaults;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0) return;

        settings.Model.PropertyChanged -= HandleModelSettingsChanged;
        presetModels.CatalogChanged -= HandleCatalogChanged;
        officialModels.CatalogChanged -= HandleCatalogChanged;
        _collection?.CollectionChanged -= HandleAssistantsChanged;

        lock (_assistantsLock)
        {
            foreach (var assistant in _assistants)
            {
                assistant.PropertyChanged -= HandleAssistantChanged;
                if (_configurations.TryGetValue(assistant, out var configuration))
                    configuration.PropertyChanged -= HandleConfigurationChanged;
            }
            _assistants.Clear();
            _configurations.Clear();
        }
    }
}
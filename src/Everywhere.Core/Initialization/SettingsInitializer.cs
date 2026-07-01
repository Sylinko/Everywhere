using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Utilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the settings with dynamic defined list.
/// Also initializes an observer that automatically saves the settings when changed.
/// </summary>
public sealed class SettingsInitializer : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Settings;

    private readonly Dictionary<string, object?> _saveBuffer = new();
    private readonly DebounceExecutor<Dictionary<string, object?>, DispatcherTimerImpl> _saveDebounceExecutor;
    private readonly Settings _settings;

    public SettingsInitializer(Settings settings, [FromKeyedServices(typeof(Settings))] IConfiguration configuration)
    {
        _settings = settings;

        _saveDebounceExecutor = new DebounceExecutor<Dictionary<string, object?>, DispatcherTimerImpl>(
            () => _saveBuffer,
            saveBuffer =>
            {
                lock (saveBuffer)
                {
                    if (saveBuffer.Count == 0) return;
                    foreach (var (key, value) in saveBuffer.AsValueEnumerable()) configuration.Set(key, value);
                    saveBuffer.Clear();
                }
            },
            TimeSpan.FromSeconds(0.5));
    }

    public Task InitializeAsync()
    {
        InitializeObserver();

        return Task.CompletedTask;
    }

    private void InitializeObserver()
    {
        new ObjectObserver(HandleSettingsChanges).Observe(_settings);

        void HandleSettingsChanges(in ObjectObserverChangedEventArgs e)
        {
            lock (_saveBuffer) _saveBuffer[e.Path] = e.Value;
            _saveDebounceExecutor.Trigger();
        }
    }
}
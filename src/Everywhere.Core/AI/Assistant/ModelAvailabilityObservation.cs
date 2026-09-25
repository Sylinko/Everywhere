using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Cloud;

namespace Everywhere.AI;

/// <summary>
/// Tracks model availability for one fixed configuration instance.
/// </summary>
/// <remarks>
/// The observation only evaluates existing catalog state. It does not refresh a catalog, schedule
/// time-based updates, or marshal notifications to a particular synchronization context.
/// </remarks>
public sealed partial class ModelAvailabilityObservation : ObservableObject, IDisposable
{
    [ObservableProperty]
    public partial ModelAvailability Value { get; private set; } = new(ModelAvailabilityKind.None, null, null);

    private readonly AssistantCatalog _catalog;
    private readonly AssistantConfiguration _configuration;
    private readonly IPresetModelProvider _presetModels;
    private readonly IOfficialModelProvider _officialModels;
    private bool _isDisposed;

    internal ModelAvailabilityObservation(
        AssistantCatalog catalog,
        AssistantConfiguration configuration,
        IPresetModelProvider presetModels,
        IOfficialModelProvider officialModels)
    {
        _catalog = catalog;
        _configuration = configuration;
        _presetModels = presetModels;
        _officialModels = officialModels;

        configuration.PropertyChanged += HandleConfigurationChanged;
        switch (configuration)
        {
            case OfficialAssistantConfiguration:
                officialModels.CatalogChanged += HandleCatalogChanged;
                officialModels.PropertyChanged += HandleOfficialModelsChanged;
                break;
            case PresetAssistantConfiguration:
                presetModels.CatalogChanged += HandleCatalogChanged;
                break;
        }

        Update();
    }

    /// <summary>
    /// Re-evaluates availability, including time-dependent deprecation state.
    /// </summary>
    public void Update()
    {
        if (_isDisposed) return;
        Value = _catalog.EvaluateAvailability(_configuration, DateOnly.FromDateTime(DateTime.Now));
    }

    private void HandleConfigurationChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AssistantConfiguration.ModelId) or
            nameof(AssistantConfiguration.DeprecationDate) or
            nameof(PresetAssistantConfiguration.ProviderId))
        {
            Update();
        }
    }

    private void HandleCatalogChanged(object? sender, EventArgs e) => Update();

    private void HandleOfficialModelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IOfficialModelProvider.AccessStatus)) Update();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _configuration.PropertyChanged -= HandleConfigurationChanged;
        switch (_configuration)
        {
            case OfficialAssistantConfiguration:
                _officialModels.CatalogChanged -= HandleCatalogChanged;
                _officialModels.PropertyChanged -= HandleOfficialModelsChanged;
                break;
            case PresetAssistantConfiguration:
                _presetModels.CatalogChanged -= HandleCatalogChanged;
                break;
        }
    }
}
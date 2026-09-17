using System.ComponentModel;
using Everywhere.Common;

namespace Everywhere.AI;

/// <summary>
/// Publishes model definitions for locally supported providers. Returned lists are stable snapshots.
/// Model publication and property notifications are delivered on the UI thread.
/// </summary>
public interface IPresetModelProvider : INotifyPropertyChanged
{
    bool IsBusy { get; }
    bool IsValidated { get; }
    event EventHandler? ModelsChanged;
    IReadOnlyList<ModelDefinitionTemplate> GetModelDefinitions(string? providerId);
    ModelDefinitionTemplate? GetValidatedModel(string? providerId, string? modelId);
    Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default);
}
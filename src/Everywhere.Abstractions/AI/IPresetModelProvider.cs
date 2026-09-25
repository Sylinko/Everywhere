using System.ComponentModel;
using Everywhere.Common;

namespace Everywhere.AI;

/// <summary>
/// Publishes model definitions for locally supported providers. Returned lists are stable snapshots.
/// Publication may arrive from a background thread; UI consumers marshal notifications at their boundary.
/// </summary>
public interface IPresetModelProvider : INotifyPropertyChanged
{
    bool IsRefreshing { get; }

    PresetModelCatalog Catalog { get; }

    event EventHandler? CatalogChanged;

    Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default);
}
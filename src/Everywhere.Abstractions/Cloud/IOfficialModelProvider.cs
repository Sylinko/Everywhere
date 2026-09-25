using System.ComponentModel;
using Everywhere.Common;

namespace Everywhere.Cloud;

/// <summary>
/// Provides official model definitions for the Everywhere AI platform.
/// </summary>
public interface IOfficialModelProvider : INotifyPropertyChanged
{
    OfficialModelCatalog Catalog { get; }

    /// <summary>
    /// Indicates whether the provider is currently fetching or refreshing the model definitions.
    /// </summary>
    bool IsRefreshing { get; }

    /// <summary>
    /// Describes whether the signed-in session can currently access official models. A pending state
    /// suppresses availability warnings while silent login is still being resolved.
    /// </summary>
    OfficialModelCatalogAccessStatus AccessStatus { get; }

    /// <summary>Raised after the catalog or its authority state changes.</summary>
    event EventHandler? CatalogChanged;

    /// <summary>
    /// Manually refresh the list of model definitions from the official source.
    /// </summary>
    /// <param name="exceptionHandler"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default);
}

public enum OfficialModelCatalogAccessStatus
{
    Pending,
    Available,
    SignInRequired
}
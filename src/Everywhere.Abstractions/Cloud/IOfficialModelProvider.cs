using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Cloud;

/// <summary>
/// Provides official model definitions for the Everywhere AI platform.
/// </summary>
public interface IOfficialModelProvider
{
    /// <summary>
    /// This should be an observable collection that notifies subscribers when the list of model definitions changes.
    /// This should refresh before & after get is called.
    /// </summary>
    IReadOnlyBindableList<ModelDefinitionTemplate> ModelDefinitions { get; }

    /// <summary>
    /// Indicates whether the provider is currently fetching or refreshing the model definitions.
    /// </summary>
    bool IsBusy { get; }

    /// <summary>
    /// Manually refresh the list of model definitions from the official source.
    /// </summary>
    /// <param name="exceptionHandler"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task RefreshAsync(IExceptionHandler? exceptionHandler = null, CancellationToken cancellationToken = default);
}

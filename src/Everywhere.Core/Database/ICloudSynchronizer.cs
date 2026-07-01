using System.ComponentModel;

namespace Everywhere.Database;

/// <summary>
/// Defines methods for synchronizing chat database with a remote source.
/// </summary>
public interface IChatDbSynchronizer : INotifyPropertyChanged
{
    /// <summary>
    /// Indicates whether a synchronization operation is currently in progress.
    /// </summary>
    bool IsCloudSyncing { get; }

    /// <summary>
    /// Manually triggers synchronization with the remote source.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}
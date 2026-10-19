using Avalonia.Threading;
using ObservableCollections;

namespace Everywhere.Common;

/// <summary>
/// Dispatches collection notifications to the Avalonia UI thread.
/// </summary>
public sealed class AvaloniaCollectionEventDispatcher : ICollectionEventDispatcher
{
    /// <summary>
    /// Shared stateless dispatcher for UI-bound collections.
    /// </summary>
    public static AvaloniaCollectionEventDispatcher Instance { get; } = new();

    /// <inheritdoc />
    public void Post(CollectionEventDispatcherEventArgs ev)
    {
        Dispatcher.UIThread.PostOnDemand(ev.Invoke);
    }
}
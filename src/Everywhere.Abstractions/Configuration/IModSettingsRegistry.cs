using Avalonia.Controls;
using Everywhere.Modding.Abstractions;

namespace Everywhere.Configuration;

/// <summary>
/// Publishes an optional mod-wide settings entry without coupling the neutral mod engine to UI.
/// This entry is independent of chat-tool settings and does not require a settings persistence engine.
/// </summary>
public interface IModSettingsRegistry
{
    /// <summary>
    /// Registers one lazy factory per mod activation. Retain the handle, register it with the context,
    /// and await its disposal to stop edits before disposing any model or persistence resources it uses.
    /// Hot disposal detaches the view on the UI thread;
    /// process-exit disposal drops ownership without requiring a live dispatcher.
    /// </summary>
    IAsyncDisposable Register(IModContext context, Func<SettingsItems> createItems);

    /// <summary>
    /// Gets or creates the active mod's settings view on the UI thread. Returns null when unavailable.
    /// Repeated calls return the same view until registration closes; no mod is activated by this call.
    /// </summary>
    Control? CreateView(string modId);
}